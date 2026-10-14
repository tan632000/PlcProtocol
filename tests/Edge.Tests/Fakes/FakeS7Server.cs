using System.Net;
using System.Net.Sockets;

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
    private const byte FunctionSetup = 0xF0;
    private const byte FunctionRead = 0x04;

    private readonly FakeS7Mode _mode;
    private readonly TcpListener? _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private readonly TaskCompletionSource _jobReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _acceptCount;
    private int _openConnections;

    private FakeS7Server(FakeS7Mode mode, TimeSpan responseDelay, byte[] readData)
    {
        _mode = mode;
        ResponseDelay = responseDelay;
        ReadData = readData;
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

                _ = ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Server stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            while (await ReadFrameAsync(stream) is { } frame)
            {
                var reply = BuildReply(frame);
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

    private byte[]? BuildReply(byte[] frame)
    {
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
            return SetupCommunicationAck;
        }

        _jobReceived.TrySetResult();
        return _mode switch
        {
            FakeS7Mode.HandshakeThenErrorHeader => ErrorHeaderReply,
            FakeS7Mode.HandshakeThenTruncatedFrame => TruncatedReply,
            FakeS7Mode.HandshakeThenItemAccessDenied => ItemReply(function, returnCode: 0x05, data: []),
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
