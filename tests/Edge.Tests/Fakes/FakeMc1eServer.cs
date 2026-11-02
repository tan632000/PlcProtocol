using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace IndustrialIot.Edge.Tests.Fakes;

/// <summary>
/// Loopback stand-in for an FX3U Ethernet adapter speaking the A-compatible 1E binary frame, with the limits of the
/// FX3U-ENET-ADP User's Manual (JY997D45801): word reads of at most 64 D registers or 32 bit-device words, word writes
/// of at most 64 D registers, bit writes of at most 160 points, a bit-device head at a multiple of 16 in word units,
/// FX3U device ranges, and the complete codes 56H/57H/58H. Every frame and command code is recorded; a frame that is
/// not a well-formed batch read in word units or batch write is recorded as unexpected. Writes change the memory.
/// </summary>
internal sealed class FakeMc1eServer : IAsyncDisposable
{
    public const ushort D = 0x4420;
    public const ushort M = 0x4D20;
    public const ushort X = 0x5820;
    public const ushort Y = 0x5920;

    private const byte BatchReadWords = 0x01;
    private const byte BatchWriteBits = 0x02;
    private const byte BatchWriteWords = 0x03;

    private static readonly Dictionary<ushort, int> MaxDevice = new()
    {
        [D] = 7999,
        [M] = 7679,
        [X] = 0xFF, // X377
        [Y] = 0xFF, // Y377
    };

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<byte[]> _frames = new();
    private readonly ConcurrentQueue<byte[]> _unexpected = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _acceptLoop;
    private readonly List<(ushort Code, int Head, int Points)> _writes = [];
    private int _connections;
    private int _pendingComplete;
    private int _dropNextWrite;
    private byte[]? _rawNext;

    public FakeMc1eServer()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>D-register contents; unset registers read as 0.</summary>
    public ConcurrentDictionary<int, ushort> Memory { get; } = new();

    /// <summary>Bit-device contents as 16-device words, keyed by device code and word index (device number / 16).</summary>
    public ConcurrentDictionary<(ushort Code, int Word), ushort> BitWords { get; } = new();

    /// <summary>D registers the adapter refuses: a read touching any of them gets complete code 56H.</summary>
    public ConcurrentDictionary<int, byte> BadDevices { get; } = new();

    public IReadOnlyList<byte[]> Frames => [.. _frames];

    /// <summary>The command code (first byte) of every frame received.</summary>
    public IReadOnlyList<byte> Commands => [.. _frames.Select(f => f[0])];

    /// <summary>Well-formed requests as (device code, head device number, points).</summary>
    public IReadOnlyList<(ushort Code, int Head, int Points)> Requests =>
        [.. _frames.Select(f => (BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(8)), BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(4)), (int)f[10]))];

    public IReadOnlyList<byte[]> UnexpectedFrames => [.. _unexpected];

    /// <summary>Batch writes applied, as (device code, head device number, points).</summary>
    public IReadOnlyList<(ushort Code, int Head, int Points)> Writes
    {
        get
        {
            lock (_writes)
            {
                return [.. _writes];
            }
        }
    }

    /// <summary>The next batch write is received and the connection is closed without a reply.</summary>
    public void DropNextWrite() => Volatile.Write(ref _dropNextWrite, 1);

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Sets one bit device; <paramref name="number"/> is the device number (X17 is 15).</summary>
    public void SetBit(ushort code, int number, bool on) =>
        BitWords.AddOrUpdate(
            (code, number >> 4),
            _ => on ? (ushort)(1 << (number & 0xF)) : (ushort)0,
            (_, old) => on ? (ushort)(old | (1 << (number & 0xF))) : (ushort)(old & ~(1 << (number & 0xF))));

    /// <summary>The next request is answered with exactly these bytes, whatever it asked.</summary>
    public void SendRawNext(params byte[] reply) => Volatile.Write(ref _rawNext, reply);

    /// <summary>The next request is answered with this complete code (5BH adds an abnormal code and 00H).</summary>
    public void FailNextWith(byte completeCode) => Volatile.Write(ref _pendingComplete, completeCode);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        lock (_clients)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }

        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // Listener shutdown; nothing to report.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Interlocked.Increment(ref _connections);
            lock (_clients)
            {
                _clients.Add(client);
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                var header = new byte[12];
                await stream.ReadExactlyAsync(header, _stop.Token);
                var points = header[10] == 0 ? 256 : header[10];
                var dataLength = header[0] switch
                {
                    BatchWriteWords => 2 * points,
                    BatchWriteBits => (points + 1) / 2,
                    _ => 0,
                };
                var frame = new byte[12 + dataLength];
                header.CopyTo(frame, 0);
                await stream.ReadExactlyAsync(frame.AsMemory(12), _stop.Token);
                _frames.Enqueue(frame);
                if (header[0] is BatchWriteWords or BatchWriteBits && Interlocked.Exchange(ref _dropNextWrite, 0) == 1)
                {
                    // The request arrived; the connection breaks before any reply.
                    client.Dispose();
                    return;
                }

                await stream.WriteAsync(Answer(frame), _stop.Token);
            }
        }
        catch (Exception)
        {
            // Client closed or server stopping.
        }
    }

    private byte[] Answer(byte[] frame)
    {
        var command = frame[0];
        var code = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(8));
        var head = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4));
        var points = frame[10];
        if (command is not (BatchReadWords or BatchWriteWords or BatchWriteBits)
            || frame[1] != 0xFF || frame[11] != 0x00 || !MaxDevice.ContainsKey(code))
        {
            _unexpected.Enqueue(frame);
            return [(byte)(command | 0x80), 0x50];
        }

        if (command is BatchWriteWords or BatchWriteBits)
        {
            return Write(frame, command, code, head, points);
        }

        if (Interlocked.Exchange(ref _rawNext, null) is { } raw)
        {
            return raw;
        }

        if (Interlocked.Exchange(ref _pendingComplete, 0) is var pending and not 0)
        {
            return pending == 0x5B ? [0x81, 0x5B, 0x10, 0x00] : [0x81, (byte)pending];
        }

        var bit = code != D;
        var max = MaxDevice[code];
        var words = points;
        if (head > max || (bit && head % 16 != 0))
        {
            return [0x81, 0x58];
        }

        if (words < 1 || words > (bit ? 32 : 64) || (bit ? head + (words * 16) - 1 : head + words - 1) > max)
        {
            return [0x81, 0x57];
        }

        if (!bit && Enumerable.Range(head, words).Any(BadDevices.ContainsKey))
        {
            return [0x81, 0x56];
        }

        return ReadReply(code, head, words);
    }

    private byte[] Write(byte[] frame, byte command, ushort code, int head, int points)
    {
        var reply = (byte)(command | 0x80);
        if (Interlocked.Exchange(ref _pendingComplete, 0) is var pending and not 0)
        {
            return pending == 0x5B ? [reply, 0x5B, 0x10, 0x00] : [reply, (byte)pending];
        }

        var bit = code != D;
        var max = MaxDevice[code];
        if (command == BatchWriteWords)
        {
            // Word units: D registers, or bit devices as 16-point words from a multiple of 16 (at most 10 words).
            if (head > max || (bit && head % 16 != 0))
            {
                return [reply, 0x58];
            }

            if (points > (bit ? 10 : 64) || (bit ? head + (points * 16) - 1 : head + points - 1) > max)
            {
                return [reply, 0x57];
            }

            for (var i = 0; i < points; i++)
            {
                var value = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(12 + (2 * i)));
                if (bit)
                {
                    BitWords[(code, (head >> 4) + i)] = value;
                }
                else
                {
                    Memory[head + i] = value;
                }
            }
        }
        else
        {
            // Bit units: bit devices only, a nibble per point, the first point in the high nibble.
            if (!bit || head > max)
            {
                return [reply, 0x58];
            }

            if (points > 160 || head + points - 1 > max)
            {
                return [reply, 0x57];
            }

            for (var i = 0; i < points; i++)
            {
                var nibble = i % 2 == 0 ? frame[12 + (i / 2)] >> 4 : frame[12 + (i / 2)] & 0x0F;
                SetBit(code, head + i, nibble != 0);
            }
        }

        lock (_writes)
        {
            _writes.Add((code, head, points));
        }

        return [reply, 0x00];
    }

    private byte[] ReadReply(ushort code, int head, int words)
    {
        var bit = code != D;
        var reply = new byte[2 + (2 * words)];
        reply[0] = 0x81;
        for (var i = 0; i < words; i++)
        {
            var value = bit ? BitWords.GetValueOrDefault((code, (head >> 4) + i)) : Memory.GetValueOrDefault(head + i);
            BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(2 + (2 * i)), value);
        }

        return reply;
    }
}
