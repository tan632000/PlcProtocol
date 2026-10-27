using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace IndustrialIot.Edge.Tests.Fakes;

public enum FakeSlmpMode
{
    /// <summary>Answers every device read from <see cref="FakeSlmpServer.Memory"/> in one write.</summary>
    Respond,

    /// <summary>Like <see cref="Respond"/> but sends the reply one byte at a time, as a fragmenting network might.</summary>
    RespondByteByByte,

    /// <summary>Answers with one word fewer than requested (a valid frame holding too little data).</summary>
    ShortReply,

    /// <summary>Answers with an ASCII-coded frame ("D0..."), as a PLC set to ASCII data code would.</summary>
    AsciiReply,

    /// <summary>Accepts connections and reads requests but never answers (outage / black hole).</summary>
    BlackHole,
}

/// <summary>
/// Loopback stand-in for an FX5U SLMP port (3E frame, binary). Requests are parsed as the PLC would; every
/// frame is recorded, and any frame that is not a Device Read (0401/0000) on one of the four device codes is
/// recorded as unexpected.
/// </summary>
internal sealed class FakeSlmpServer : IAsyncDisposable
{
    private static readonly byte[] DeviceCodes = [0xA8, 0x90, 0x9C, 0x9D];

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<byte[]> _frames = new();
    private readonly ConcurrentQueue<byte[]> _unexpected = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _acceptLoop;
    private int _connections;
    private int _pendingErrorEndCode;

    public FakeSlmpServer()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public volatile FakeSlmpMode Mode = FakeSlmpMode.Respond;

    /// <summary>D-register contents; unset devices read as 0.</summary>
    public ConcurrentDictionary<int, ushort> Memory { get; } = new();

    /// <summary>
    /// When set, a read touching a D device above this number is answered with <see cref="RangeEndCode"/>,
    /// as the PLC does for a device beyond its configured range. Null: no limit.
    /// </summary>
    public int? MaxDevice { get; set; }

    /// <summary>Bit-device contents as 16-device words, keyed by device code and word index (device number / 16).</summary>
    public ConcurrentDictionary<(byte Code, int Word), ushort> BitWords { get; } = new();

    /// <summary>Sets one bit device; <paramref name="number"/> is the device number (X17 is 15).</summary>
    public void SetBit(byte code, int number, bool on) =>
        BitWords.AddOrUpdate(
            (code, number >> 4),
            _ => on ? (ushort)(1 << (number & 0xF)) : (ushort)0,
            (_, old) => on ? (ushort)(old | (1 << (number & 0xF))) : (ushort)(old & ~(1 << (number & 0xF))));

    /// <summary>D devices the PLC refuses: a read touching any of them gets <see cref="RangeEndCode"/>.</summary>
    public ConcurrentDictionary<int, byte> BadDevices { get; } = new();

    public ushort RangeEndCode { get; set; } = 0xC056;

    /// <summary>Every request received, as raw bytes, in arrival order.</summary>
    public IReadOnlyList<byte[]> Frames => [.. _frames];

    /// <summary>Requests as (device code, head, points).</summary>
    public IReadOnlyList<(byte Code, int Head, int Points)> Requests =>
        [.. _frames.Select(f => (f[18], f[15] | (f[16] << 8) | (f[17] << 16), (int)BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(19))))];

    public IReadOnlyList<byte[]> UnexpectedFrames => [.. _unexpected];

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>The next request is answered with this end code plus 9 bytes of error information.</summary>
    public void FailNextWith(ushort endCode) => Volatile.Write(ref _pendingErrorEndCode, endCode);

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
                var header = new byte[9];
                await stream.ReadExactlyAsync(header, _stop.Token);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(7));
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, _stop.Token);

                var frame = header.Concat(body).ToArray();
                _frames.Enqueue(frame);

                // Anything but a 21-byte Device Read on a known device code is recorded, never answered as a read.
                if (frame.Length != 21
                    || frame[0] != 0x50
                    || frame[1] != 0x00
                    || !frame.AsSpan(2, 5).SequenceEqual(new byte[] { 0x00, 0xFF, 0xFF, 0x03, 0x00 })
                    || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(7)) != 12
                    || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11)) != 0x0401
                    || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(13)) != 0x0000
                    || !DeviceCodes.Contains(frame[18])
                    // A word-unit read of a bit device must start on a 16-device boundary.
                    || (frame[18] != 0xA8 && (frame[15] & 0x0F) != 0))
                {
                    _unexpected.Enqueue(frame);
                    continue;
                }

                if (Mode == FakeSlmpMode.BlackHole)
                {
                    continue;
                }

                var reply = BuildReply(frame);
                if (Mode == FakeSlmpMode.RespondByteByByte)
                {
                    foreach (var b in reply)
                    {
                        await stream.WriteAsync(new[] { b }, _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                        await Task.Delay(1, _stop.Token);
                    }
                }
                else
                {
                    await stream.WriteAsync(reply, _stop.Token);
                }
            }
        }
        catch (Exception)
        {
            // Client closed the connection or the server is stopping.
        }
    }

    private byte[] BuildReply(byte[] frame)
    {
        var head = frame[15] | (frame[16] << 8) | (frame[17] << 16);
        var points = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(19));

        var errorEndCode = (ushort)Interlocked.Exchange(ref _pendingErrorEndCode, 0);
        if (errorEndCode == 0 && MaxDevice is { } max && frame[18] == 0xA8 && head + points - 1 > max)
        {
            errorEndCode = RangeEndCode;
        }

        if (errorEndCode == 0 && frame[18] == 0xA8 && Enumerable.Range(head, points).Any(BadDevices.ContainsKey))
        {
            errorEndCode = RangeEndCode;
        }

        byte[] data;
        if (errorEndCode != 0)
        {
            // End code + error information: network, PC, module I/O (2), station, command (2), subcommand (2).
            data = new byte[11];
            BinaryPrimitives.WriteUInt16LittleEndian(data, errorEndCode);
            data[2] = 0x00;
            data[3] = 0xFF;
            data[4] = 0xFF;
            data[5] = 0x03;
            data[6] = 0x00;
            frame.AsSpan(11, 4).CopyTo(data.AsSpan(7));
        }
        else
        {
            var sent = Mode == FakeSlmpMode.ShortReply ? Math.Max(points - 1, 0) : points;
            data = new byte[2 + (2 * sent)];
            for (var i = 0; i < sent; i++)
            {
                var word = frame[18] == 0xA8
                    ? Memory.GetValueOrDefault(head + i)
                    : BitWords.GetValueOrDefault((frame[18], (head >> 4) + i));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 + (2 * i)), word);
            }
        }

        if (Mode == FakeSlmpMode.AsciiReply)
        {
            return System.Text.Encoding.ASCII.GetBytes("D00000FF03FF000004" + "0000");
        }

        var reply = new byte[9 + data.Length];
        reply[0] = 0xD0;
        reply[1] = 0x00;
        reply[2] = 0x00;
        reply[3] = 0xFF;
        reply[4] = 0xFF;
        reply[5] = 0x03;
        reply[6] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(7), (ushort)data.Length);
        data.CopyTo(reply, 9);
        return reply;
    }
}
