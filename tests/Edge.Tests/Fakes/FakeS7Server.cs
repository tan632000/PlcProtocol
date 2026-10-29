using System.Net;
using System.Net.Sockets;
using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Tests.Fakes;

public enum FakeS7Mode
{
    /// <summary>Nothing listens on the port.</summary>
    ClosedPort,

    /// <summary>Accepts TCP but never replies, not even to the COTP connection request.</summary>
    BlackHole,

    /// <summary>Completes the COTP + S7 setup handshake, then never answers a read/write job.</summary>
    HandshakeThenSilent,

    /// <summary>Answers every job with a header-only error reply, error class 0x81 code 0x04 (PUT/GET disabled).</summary>
    HandshakeThenErrorHeader,

    /// <summary>Answers every job with item return code 0x05.</summary>
    HandshakeThenItemAccessDenied,

    /// <summary>Answers every job with a well-framed but truncated TPKT (COTP header cut short), as a broken middlebox might.</summary>
    HandshakeThenTruncatedFrame,

    /// <summary>Answers reads with <see cref="FakeS7Server.ReadData"/> and acknowledges writes, after <see cref="FakeS7Server.ResponseDelay"/>.</summary>
    HandshakeThenRespond,
}

/// <summary>What the fake does with one scripted job instead of answering it.</summary>
public enum FakeS7JobFault
{
    /// <summary>Keeps the socket open and never answers.</summary>
    Silent,

    /// <summary>Closes the socket without answering.</summary>
    Close,

    /// <summary>Answers a memory read, but the last item carries one data byte less than requested.</summary>
    ShortLastItem,
}

/// <summary>One job received after the handshake: connection number (1-based), function code and item count.</summary>
internal sealed record FakeS7Job(int Connection, byte Function, int ItemCount);

/// <summary>
/// Loopback stand-in for an S7-1200. Frames are taken from S7netplus 0.20.0 (its unit-test templates for
/// COTP CC and setup-communication, and the reply layout read by <c>AssertReadResponse</c>/<c>ValidateResponseCode</c>).
/// </summary>
internal sealed class FakeS7Server : IAsyncDisposable
{
    // TPKT(4) + COTP CC(7).
    private static readonly byte[] ConnectionConfirm = [0x03, 0x00, 0x00, 0x0B, 0x06, 0xD0, 0x00, 0x00, 0x00, 0x00, 0x00];

    // TPKT(4) + COTP DT(3) + S7 ack-data header(12) + setup parameters(8); PDU size 960.
    private static readonly byte[] SetupCommunicationAck =
    [
        0x03, 0x00, 0x00, 0x1B, 0x02, 0xF0, 0x80,
        0x32, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00,
        0xF0, 0x00, 0x00, 0x03, 0x00, 0x03, 0x03, 0xC0,
    ];

    // Header-only ack-data: no parameters, no data, error class 0x81 / code 0x04.
    private static readonly byte[] ErrorHeaderReply =
    [
        0x03, 0x00, 0x00, 0x13, 0x02, 0xF0, 0x80,
        0x32, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x81, 0x04,
    ];

    // TPKT length 6: COTP says "data" but the flags byte is missing, so S7netplus fails while parsing the frame.
    private static readonly byte[] TruncatedReply = [0x03, 0x00, 0x00, 0x06, 0x02, 0xF0];

    private const int FrameFunctionCodeOffset = 17;   // TPKT(4) + COTP DT(3) + S7 job header(10)
    private const int FrameReadLengthOffset = 23;     // function, count, 12 0A 10, transport size, then length
    private const int FrameItemCountOffset = 18;
    private const int FrameFirstItemOffset = 19;
    private const int ItemSpecLength = 12;
    private const int SetupPduOffset = 25;            // TPKT(4) + COTP DT(3) + S7 header(12) + F0 00 00 03 00 03
    private const byte FunctionSetup = 0xF0;
    private const byte FunctionRead = 0x04;
    private const byte DefaultMemoryByte = 0xEE;

    private readonly FakeS7Mode _mode;
    private readonly TcpListener? _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private readonly TaskCompletionSource _jobReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<FakeS7Job> _jobs = [];
    private readonly Dictionary<(byte Area, int Db), byte[]>? _memory;
    private readonly Dictionary<(byte Area, int Db, int Byte), byte> _refused = [];
    private readonly Dictionary<int, FakeS7JobFault> _faults = [];
    private int[] _pduSizes = [960];
    private int _acceptCount;
    private int _openConnections;

    private FakeS7Server(FakeS7Mode mode, TimeSpan responseDelay, byte[] readData, bool addressAware = false)
    {
        _mode = mode;
        ResponseDelay = responseDelay;
        ReadData = readData;
        _memory = addressAware ? [] : null;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;

        if (mode == FakeS7Mode.ClosedPort)
        {
            // Reserve a free port number, then release it so connections are refused.
            listener.Stop();
            return;
        }

        _listener = listener;
        _ = AcceptLoopAsync();
    }

    public int Port { get; }

    public int AcceptCount => Volatile.Read(ref _acceptCount);

    /// <summary>Connections accepted and not yet closed by the client.</summary>
    public int OpenConnections => Volatile.Read(ref _openConnections);

    /// <summary>Delay before every reply (handshake included).</summary>
    public TimeSpan ResponseDelay { get; }

    /// <summary>Bytes returned to reads, truncated to the requested length.</summary>
    public byte[] ReadData { get; }

    /// <summary>Completes when the first read or write job arrives (after the handshake).</summary>
    public Task JobReceived => _jobReceived.Task;

    public static FakeS7Server Start(FakeS7Mode mode, TimeSpan responseDelay = default, byte[]? readData = null) =>
        new(mode, responseDelay, readData ?? [0x00, 0x00, 0x00, 0x00]);

    /// <summary>
    /// Answers read jobs item by item from per-(area, DB) memory, like a PLC: each item gets its own return code,
    /// and a fill byte follows every odd-length item except the last. Unseeded bytes read as 0xEE.
    /// </summary>
    public static FakeS7Server StartWithMemory(int pduSize, TimeSpan responseDelay = default)
    {
        var server = new FakeS7Server(FakeS7Mode.HandshakeThenRespond, responseDelay, [], addressAware: true);
        server.SetPduSizes(pduSize);
        return server;
    }

    /// <summary>The last reply built from memory (whole TPKT frame).</summary>
    public byte[]? LastReply { get; private set; }

    /// <summary>Every job received after the handshake, in arrival order.</summary>
    public IReadOnlyList<FakeS7Job> Jobs
    {
        get
        {
            lock (_jobs)
            {
                return [.. _jobs];
            }
        }
    }

    /// <summary>PDU size offered in the setup reply of connection 1, 2, ...; the last value repeats.</summary>
    public void SetPduSizes(params int[] perConnection) => Volatile.Write(ref _pduSizes, perConnection);

    /// <summary>Stores <paramref name="bytes"/> at the start of an S7 address, e.g. <c>Seed("DB1.DBW2", 0x12, 0x34)</c>.</summary>
    public void Seed(string address, params byte[] bytes)
    {
        var (area, db, start) = Locate(address);
        lock (_memory!)
        {
            bytes.CopyTo(MemoryOf(area, db), start);
        }
    }

    /// <summary>Items starting at this address get <paramref name="returnCode"/> instead of data until <see cref="Accept"/>.</summary>
    public void Refuse(string address, byte returnCode)
    {
        lock (_refused)
        {
            _refused[Locate(address)] = returnCode;
        }
    }

    public void Accept(string address)
    {
        lock (_refused)
        {
            _refused.Remove(Locate(address));
        }
    }

    /// <summary>Job number <paramref name="jobNumber"/> (1-based, counted over all connections) gets <paramref name="fault"/> instead of a reply.</summary>
    public void FailJob(int jobNumber, FakeS7JobFault fault)
    {
        lock (_faults)
        {
            _faults[jobNumber] = fault;
        }
    }

    public async Task WaitForJobsAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Jobs.Count < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} job(s), got {Jobs.Count}.");
            }

            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener?.Stop();
        lock (_clients)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener!.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _acceptCount);
                Interlocked.Increment(ref _openConnections);
                lock (_clients)
                {
                    _clients.Add(client);
                }

                _ = ServeAsync(client, AcceptCount);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Server stopped.
        }
    }

    private async Task ServeAsync(TcpClient client, int connection)
    {
        try
        {
            var stream = client.GetStream();
            while (await ReadFrameAsync(stream) is { } frame)
            {
                var reply = BuildReply(frame, connection, out var fault);
                if (fault == FakeS7JobFault.Close)
                {
                    client.Close();
                    return;
                }

                if (reply is null)
                {
                    continue;
                }

                if (ResponseDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ResponseDelay, _stop.Token);
                }

                await stream.WriteAsync(reply, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Client went away or server stopped.
        }
        finally
        {
            Interlocked.Decrement(ref _openConnections);
        }
    }

    private byte[]? BuildReply(byte[] frame, int connection, out FakeS7JobFault? fault)
    {
        fault = null;
        if (_mode == FakeS7Mode.BlackHole)
        {
            return null;
        }

        var isConnectionRequest = frame.Length > 5 && frame[5] == 0xE0;
        if (isConnectionRequest)
        {
            return ConnectionConfirm;
        }

        var function = frame.Length > FrameFunctionCodeOffset ? frame[FrameFunctionCodeOffset] : (byte)0;
        if (function == FunctionSetup)
        {
            return SetupReply(connection);
        }

        int jobNumber;
        lock (_jobs)
        {
            _jobs.Add(new FakeS7Job(connection, function, frame.Length > FrameItemCountOffset ? frame[FrameItemCountOffset] : 0));
            jobNumber = _jobs.Count;
        }

        _jobReceived.TrySetResult();
        lock (_faults)
        {
            if (_faults.Remove(jobNumber, out var scripted))
            {
                if (scripted == FakeS7JobFault.ShortLastItem)
                {
                    return LastReply = MemoryReadReply(frame, shortLastItem: true);
                }

                fault = scripted;
                return null;
            }
        }

        return _mode switch
        {
            FakeS7Mode.HandshakeThenErrorHeader => ErrorHeaderReply,
            FakeS7Mode.HandshakeThenTruncatedFrame => TruncatedReply,
            FakeS7Mode.HandshakeThenItemAccessDenied => ItemReply(function, returnCode: 0x05, data: []),
            FakeS7Mode.HandshakeThenRespond when function == FunctionRead && _memory is not null => LastReply = MemoryReadReply(frame),
            FakeS7Mode.HandshakeThenRespond when function == FunctionRead =>
                ItemReply(function, returnCode: 0xFF, data: ReadData.AsSpan(0, RequestedLength(frame)).ToArray()),
            FakeS7Mode.HandshakeThenRespond => ItemReply(function, returnCode: 0xFF, data: []),
            _ => null,
        };
    }

    /// <summary>Ack-data reply with one item: return code, then (for reads) transport size, bit length and data.</summary>
    private static byte[] ItemReply(byte function, byte returnCode, byte[] data)
    {
        var isRead = function == FunctionRead;
        var dataLength = isRead ? 4 + data.Length : 1;
        var s7 = new List<byte>
        {
            0x32, 0x03, 0x00, 0x00, 0x00, 0x00,       // protocol id, ack data, reserved, PDU ref
            0x00, 0x02,                                // parameter length
            (byte)(dataLength >> 8), (byte)dataLength, // data length
            0x00, 0x00,                                // error class / code: none
            function, 0x01,                            // parameters: function, item count
            returnCode,
        };

        if (isRead)
        {
            var bits = data.Length * 8;
            s7.AddRange([0x04, (byte)(bits >> 8), (byte)bits]);
            s7.AddRange(data);
        }

        var length = 7 + s7.Count;
        byte[] header = [0x03, 0x00, (byte)(length >> 8), (byte)length, 0x02, 0xF0, 0x80];
        return [.. header, .. s7];
    }

    private byte[] SetupReply(int connection)
    {
        var sizes = Volatile.Read(ref _pduSizes);
        var pdu = sizes[Math.Min(connection, sizes.Length) - 1];
        var reply = SetupCommunicationAck.ToArray();
        reply[SetupPduOffset] = (byte)(pdu >> 8);
        reply[SetupPduOffset + 1] = (byte)pdu;
        return reply;
    }

    /// <summary>
    /// Ack-data reply for an n-item read, laid out as an S7-1200 does: per item a return code, transport size
    /// (0x04 = bytes, length in bits) and the data, or the return code and three zero bytes for a refused item;
    /// a fill byte follows odd-length data unless it is the last item.
    /// </summary>
    private byte[] MemoryReadReply(byte[] frame, bool shortLastItem = false)
    {
        var itemCount = frame[FrameItemCountOffset];
        var data = new List<byte>();
        for (var i = 0; i < itemCount; i++)
        {
            var item = frame.AsSpan(FrameFirstItemOffset + (i * ItemSpecLength), ItemSpecLength);
            var length = (item[4] << 8) | item[5];
            if (shortLastItem && i == itemCount - 1)
            {
                length--;
            }

            var db =(item[6] << 8) | item[7];
            var area = item[8];
            var start = ((item[9] << 16) | (item[10] << 8) | item[11]) >> 3;

            byte? refusedCode;
            lock (_refused)
            {
                refusedCode = _refused.TryGetValue((area, db, start), out var code) ? code : null;
            }

            if (refusedCode is { } returnCode)
            {
                data.AddRange([returnCode, 0x00, 0x00, 0x00]);
                continue;
            }

            var bits = length * 8;
            data.AddRange([0xFF, 0x04, (byte)(bits >> 8), (byte)bits]);
            lock (_memory!)
            {
                data.AddRange(MemoryOf(area, db).AsSpan(start, length).ToArray());
            }

            if (length % 2 == 1 && i < itemCount - 1)
            {
                data.Add(0x00);
            }
        }

        return AckData(FunctionRead, itemCount, [.. data]);
    }

    /// <summary>TPKT + COTP DT + S7 ack-data header + parameters (function, item count) + data.</summary>
    internal static byte[] AckData(byte function, int itemCount, byte[] data)
    {
        byte[] s7 =
        [
            0x32, 0x03, 0x00, 0x00, 0x00, 0x00,         // protocol id, ack data, reserved, PDU ref
            0x00, 0x02,                                  // parameter length
            (byte)(data.Length >> 8), (byte)data.Length, // data length
            0x00, 0x00,                                  // error class / code: none
            function, (byte)itemCount,
            .. data,
        ];
        var length = 7 + s7.Length;
        return [0x03, 0x00, (byte)(length >> 8), (byte)length, 0x02, 0xF0, 0x80, .. s7];
    }

    /// <summary>Caller holds the <c>_memory</c> lock.</summary>
    private byte[] MemoryOf(byte area, int db)
    {
        if (!_memory!.TryGetValue((area, db), out var memory))
        {
            memory = new byte[65536 + 8];
            Array.Fill(memory, DefaultMemoryByte);
            _memory[(area, db)] = memory;
        }

        return memory;
    }

    /// <summary>S7 area code (as sent in a read item), DB number and start byte of a tag address.</summary>
    private static (byte Area, int Db, int Byte) Locate(string address)
    {
        var parsed = PlcAddressParser.Parse(address);
        byte area = parsed.Area switch
        {
            PlcArea.Input => 0x81,
            PlcArea.Output => 0x82,
            PlcArea.Memory => 0x83,
            _ => 0x84,
        };
        return (area, parsed.DbNumber, parsed.ByteOffset);
    }

    private static int RequestedLength(byte[] frame) => (frame[FrameReadLengthOffset] << 8) | frame[FrameReadLengthOffset + 1];

    /// <summary>Reads one TPKT frame; returns <c>null</c> when the client closed the connection.</summary>
    private async Task<byte[]?> ReadFrameAsync(NetworkStream stream)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header))
        {
            return null;
        }

        var frame = new byte[(header[2] << 8) | header[3]];
        header.CopyTo(frame, 0);
        return await ReadExactAsync(stream, frame.AsMemory(4)) ? frame : null;
    }

    private async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var read = await stream.ReadAsync(buffer, _stop.Token);
            if (read == 0)
            {
                return false;
            }

            buffer = buffer[read..];
        }

        return true;
    }
}
