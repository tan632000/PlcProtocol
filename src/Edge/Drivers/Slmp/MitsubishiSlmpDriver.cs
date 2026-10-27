using System.Buffers.Binary;
using System.Net.Sockets;
using IndustrialIot.Edge.Drivers.Models;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Drivers.Slmp;

/// <summary>
/// <see cref="IPlcDriver"/> for a Mitsubishi FX5U over SLMP, 3E frame, binary code. The only frame it can build is
/// "Device Read" in word units (<see cref="BuildDeviceReadWords"/>); the command and subcommand are private
/// constants and no parameter carries a command. One TCP connection is kept and one request runs at a time,
/// because each SLMP connection on the PLC serves a single partner and replies carry no request id.
/// </summary>
public sealed class MitsubishiSlmpDriver : IPlcDriver, IAsyncDisposable
{
    private const int RequestLength = 21;
    private const int ResponseHeaderLength = 9;
    private const ushort ReadCommand = 0x0401;
    private const ushort WordUnitsSubcommand = 0x0000;

    // Error replies carry 9 bytes of error information after the end code; anything longer than a full
    // read is not a reply we understand.
    private const int MaxResponseDataLength = 2 + (2 * SlmpBlockPlanner.MaxBlockWords);

    // End codes meaning "this address or range is wrong", which justify finding the bad tags one by one.
    // [UNVERIFIED - SH-081951] Busy, timer and other PLC-side codes must never fan out.
    private static readonly ushort[] AddressEndCodes = [0xC056];

    private const string BinaryHint =
        "In GX Works3 the SLMP connection must use \"Communication Data Code = Binary\".";

    private readonly SlmpOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Only the lock holder reads or replaces these. The stream owns the socket and is used for reading only.
    private Socket? _socket;
    private NetworkStream? _stream;
    private volatile bool _disposed;

    // Tags the PLC refused with an address end code; touched only by ReadAsync (one poller cycle at a time).
    private readonly HashSet<string> _badTags = new(StringComparer.Ordinal);

    public MitsubishiSlmpDriver(IOptions<SlmpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(_options.TimeoutMs);

    public async Task<IReadOnlyList<PlcReadResult>> ReadAsync(IReadOnlyList<ResolvedTag> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var results = new PlcReadResult[tags.Count];
        var slots = new List<SlmpTagSlot>(tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            if (tags[i].Address is SlmpAddress address)
            {
                slots.Add(new SlmpTagSlot(i, tags[i], address, address.IsBit ? 1 : SlmpBlockPlanner.WordCount(tags[i].DataType)));
            }
            else
            {
                results[i] = PlcReadResult.Fail(new PlcAccessDeniedException($"Address '{tags[i].Address.Canonical}' is not an SLMP address."));
            }
        }

        // Remembered bad tags are read alone; the rest are merged into blocks again.
        var bad = slots.Where(s => _badTags.Contains(s.Tag.TagId)).ToList();
        PlcUnavailableException? unavailable = null;
        foreach (var block in SlmpBlockPlanner.Plan(slots.Where(s => !_badTags.Contains(s.Tag.TagId))))
        {
            if (unavailable is not null)
            {
                FailAll(block.Slots, results, unavailable);
                continue;
            }

            unavailable = await ReadBlockAsync(block, results, ct).ConfigureAwait(false);
        }

        foreach (var slot in bad)
        {
            if (unavailable is not null)
            {
                results[slot.Index] = PlcReadResult.Fail(unavailable);
                continue;
            }

            unavailable = await ReadSlotsAloneAsync([slot], slot.Address.Device, results, ct).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>Always fails: this driver cannot write.</summary>
    public Task WriteAsync(IPlcAddress address, PlcDataType type, object value, CancellationToken ct) =>
        Task.FromException(new PlcAccessDeniedException(SlmpAddress.ReadOnlyText));

    /// <summary>Returns <c>false</c> when the PLC is unreachable or the driver is busy; throws only on caller cancellation or disposal.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(Timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            await AcquireAsync(operation.Token, ct).ConfigureAwait(false);
        }
        catch (PlcUnavailableException)
        {
            return false;
        }

        try
        {
            await ConnectAsync(operation.Token, ct).ConfigureAwait(false);
            return true;
        }
        catch (PlcUnavailableException)
        {
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Waiting for the holder is bounded: every operation ends within its timeout.
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            Discard();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// SLMP 3E binary "Device Read" in word units. Layout (little-endian): subheader 50 00, network 00, PC FF,
    /// module I/O FF 03, station 00, data length, monitoring timer, command 01 04, subcommand 00 00,
    /// head device (3 bytes), device code, number of points.
    /// [UNVERIFIED until checked against the iQ-F SLMP manual SH-081951 and a real FX5U.]
    /// </summary>
    internal byte[] BuildDeviceReadWords(SlmpDevice device, int head, int count)
    {
        if (!Enum.IsDefined(device))
        {
            throw new ArgumentOutOfRangeException(nameof(device));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(head);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(head, 0xFFFFFF);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, SlmpBlockPlanner.MaxBlockWords);

        var frame = new byte[RequestLength];
        frame[0] = 0x50;
        frame[1] = 0x00;
        frame[2] = 0x00;
        frame[3] = 0xFF;
        frame[4] = 0xFF;
        frame[5] = 0x03;
        frame[6] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(7), RequestLength - 9);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(9), MonitoringTimer(_options.TimeoutMs));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(11), ReadCommand);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(13), WordUnitsSubcommand);
        frame[15] = (byte)head;
        frame[16] = (byte)(head >> 8);
        frame[17] = (byte)(head >> 16);
        frame[18] = (byte)device;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(19), (ushort)count);
        return frame;
    }

    /// <summary>
    /// PLC-side wait in 250 ms units, kept 500 ms below the client budget so a slow PLC answers with its own
    /// timeout end code before the client gives up and closes the socket.
    /// </summary>
    internal static ushort MonitoringTimer(int timeoutMs) => (ushort)Math.Max(1, (timeoutMs - 500) / 250);

    /// <summary>
    /// Reads one block of good tags with one request. When the PLC refuses it with an address end code, the block
    /// is split once into single-tag reads to find the bad tags, which are remembered; any other end code gives
    /// every tag of the block that error with no fan-out. Returns the connection failure that stopped the batch.
    /// </summary>
    private async Task<PlcUnavailableException?> ReadBlockAsync(SlmpBlock block, PlcReadResult[] results, CancellationToken ct)
    {
        WordsReply reply;
        try
        {
            reply = await ReadWordsAsync(block.Device, block.Head, block.Count, ct).ConfigureAwait(false);
        }
        catch (PlcUnavailableException ex)
        {
            FailAll(block.Slots, results, ex);
            return ex;
        }

        if (reply.EndCode == 0)
        {
            foreach (var slot in block.Slots)
            {
                Complete(slot, block.Head, reply.Words, results);
            }

            return null;
        }

        var isAddressError = AddressEndCodes.Contains(reply.EndCode);
        if (!isAddressError || block.Slots.Count == 1)
        {
            FailAll(block.Slots, results, new PlcAccessDeniedException(DeniedMessage(reply.EndCode, block.Device, block.Head, block.Count)));
            if (isAddressError)
            {
                _badTags.Add(block.Slots[0].Tag.TagId);
            }

            return null;
        }

        return await ReadSlotsAloneAsync(block.Slots, block.Device, results, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads each slot alone. A tag the PLC refuses with an address end code is remembered as bad; one that reads
    /// again is forgotten. Returns the connection failure that stopped the batch, if any.
    /// </summary>
    private async Task<PlcUnavailableException?> ReadSlotsAloneAsync(
        IReadOnlyList<SlmpTagSlot> slots, SlmpDevice device, PlcReadResult[] results, CancellationToken ct)
    {
        PlcUnavailableException? unavailable = null;
        foreach (var slot in slots)
        {
            if (unavailable is not null)
            {
                results[slot.Index] = PlcReadResult.Fail(unavailable);
                continue;
            }

            try
            {
                var reply = await ReadWordsAsync(device, slot.FirstWord, slot.Words, ct).ConfigureAwait(false);
                if (reply.EndCode == 0)
                {
                    _badTags.Remove(slot.Tag.TagId);
                    Complete(slot, slot.FirstWord, reply.Words, results);
                    continue;
                }

                if (AddressEndCodes.Contains(reply.EndCode))
                {
                    _badTags.Add(slot.Tag.TagId);
                }

                results[slot.Index] = PlcReadResult.Fail(
                    new PlcAccessDeniedException(DeniedMessage(reply.EndCode, device, slot.FirstWord, slot.Words)));
            }
            catch (PlcUnavailableException ex)
            {
                unavailable = ex;
                results[slot.Index] = PlcReadResult.Fail(ex);
            }
        }

        return unavailable;
    }

    private static void Complete(SlmpTagSlot slot, int firstWord, ushort[] words, PlcReadResult[] results)
    {
        var own = words.AsSpan(slot.FirstWord - firstWord, slot.Words);
        results[slot.Index] = PlcReadResult.Ok(
            slot.Address.IsBit
                ? SlmpBlockPlanner.DecodeBit(slot.Address.Number, own[0])
                : SlmpBlockPlanner.Decode(slot.Tag.DataType, own));
    }

    private static void FailAll(IReadOnlyList<SlmpTagSlot> slots, PlcReadResult[] results, Exception error)
    {
        foreach (var slot in slots)
        {
            results[slot.Index] = PlcReadResult.Fail(error);
        }
    }

    /// <summary>Device range of <paramref name="count"/> words from word <paramref name="word"/>, as GX Works3 spells it.</summary>
    private static string Range(SlmpDevice device, int word, int count)
    {
        var first = SlmpBlockPlanner.HeadDevice(device, word);
        var last = SlmpBlockPlanner.HeadDevice(device, word + count) - 1;
        return $"{SlmpAddress.Spell(device, first)}\u2013{SlmpAddress.Spell(device, last)}";
    }

    private static string DeniedMessage(ushort endCode, SlmpDevice device, int word, int count)
    {
        var hint = endCode switch
        {
            0xC056 => " Device out of range (hint from the manual, UNVERIFIED): check the device range in GX Works3.",
            0xC059 => " Command not supported (hint from the manual, UNVERIFIED): check the module and its SLMP settings.",
            0xC05C => " Request error (hint from the manual, UNVERIFIED): check the request contents and device numbers.",
            _ => "",
        };
        return $"SLMP end code 0x{endCode:X4} reading {Range(device, word, count)}.{hint}";
    }

    private readonly record struct WordsReply(ushort EndCode, ushort[] Words);

    /// <summary>One Device Read request. An error end code is a normal reply here; a lost or invalid reply throws.</summary>
    private async Task<WordsReply> ReadWordsAsync(SlmpDevice device, int word, int count, CancellationToken ct)
    {
        var request = BuildDeviceReadWords(device, SlmpBlockPlanner.HeadDevice(device, word), count);
        var description = $"reading {count} word(s) from {SlmpAddress.Spell(device, SlmpBlockPlanner.HeadDevice(device, word))}";

        using var budget = new CancellationTokenSource(Timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await AcquireAsync(operation.Token, ct).ConfigureAwait(false);
        try
        {
            var (socket, stream) = await ConnectAsync(operation.Token, ct).ConfigureAwait(false);

            ushort endCode;
            byte[] data;
            try
            {
                await SendAllAsync(socket, request, operation.Token).ConfigureAwait(false);

                // TCP may deliver the reply in several pieces: read exactly the header, then exactly the
                // length it announces, so no byte of this reply is left behind for the next request.
                var header = new byte[ResponseHeaderLength];
                await stream.ReadExactlyAsync(header, operation.Token).ConfigureAwait(false);
                if (header[0] != 0xD0 || header[1] != 0x00)
                {
                    throw Protocol($"reply to {description} does not start with subheader D0 00. {BinaryHint}");
                }

                if (!header.AsSpan(2, 5).SequenceEqual(request.AsSpan(2, 5)))
                {
                    throw Protocol($"reply to {description} addresses another station (header bytes 2-6 differ from the request).");
                }

                var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(7));
                if (dataLength < 2 || dataLength > MaxResponseDataLength)
                {
                    throw Protocol($"reply to {description} announces an invalid data length {dataLength}.");
                }

                data = new byte[dataLength];
                await stream.ReadExactlyAsync(data, operation.Token).ConfigureAwait(false);
                endCode = BinaryPrimitives.ReadUInt16LittleEndian(data);
            }
            catch (PlcUnavailableException)
            {
                Discard();
                throw;
            }
            catch (Exception ex)
            {
                Discard();
                ct.ThrowIfCancellationRequested();
                throw new PlcUnavailableException($"{char.ToUpperInvariant(description[0])}{description[1..]} from {_options.Host}:{_options.Port} failed: {ex.Message}", ex);
            }

            if (endCode != 0)
            {
                // The error information that follows the end code has been read, so the socket is still in step.
                return new WordsReply(endCode, []);
            }

            if (data.Length != 2 + (2 * count))
            {
                Discard();
                throw Protocol($"reply to {description} carries {data.Length - 2} data byte(s), expected {2 * count}.");
            }

            return new WordsReply(0, SlmpBlockPlanner.ToWords(data.AsSpan(2)));
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task AcquireAsync(CancellationToken operationToken, CancellationToken callerToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await _lock.WaitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            callerToken.ThrowIfCancellationRequested();
            throw new PlcBusyException($"Timed out after {_options.TimeoutMs} ms waiting for another PLC operation to finish.");
        }

        if (_disposed)
        {
            _lock.Release();
            throw new ObjectDisposedException(nameof(MitsubishiSlmpDriver));
        }
    }

    /// <summary>Returns the open connection or opens a new one within the operation budget. Caller holds the lock.</summary>
    private async Task<(Socket Socket, NetworkStream Stream)> ConnectAsync(CancellationToken operationToken, CancellationToken callerToken)
    {
        if (_socket is { } openSocket && _stream is { } openStream)
        {
            return (openSocket, openStream);
        }

        try
        {
            // WaitAsync bounds the connect even if the OS ignores the token (a SYN to a powered-off PLC
            // can otherwise hang for about 21 s on Windows).
            var socket = await ConnectTcpAsync(operationToken).WaitAsync(operationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            (_socket, _stream) = (socket, stream);
            return (socket, stream);
        }
        catch (Exception ex)
        {
            callerToken.ThrowIfCancellationRequested();
            throw new PlcUnavailableException($"Cannot connect to PLC {_options.Host}:{_options.Port}: {ex.Message}", ex);
        }
    }

    private async Task<Socket> ConnectTcpAsync(CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        // Detects a peer that vanished without closing (cable pulled, PLC powered off) on an idle connection.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        try
        {
            await socket.ConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void Discard()
    {
        var stream = _stream;
        var socket = _socket;
        _stream = null;
        _socket = null;
        stream?.Dispose();
        socket?.Dispose();
    }

    /// <summary>Sends the whole request; a stream socket may accept fewer bytes per call than offered.</summary>
    private static async Task SendAllAsync(Socket socket, byte[] request, CancellationToken ct)
    {
        var sent = 0;
        while (sent < request.Length)
        {
            sent += await socket.SendAsync(request.AsMemory(sent), SocketFlags.None, ct).ConfigureAwait(false);
        }
    }

    private static PlcUnavailableException Protocol(string message) => new($"Invalid SLMP reply: {message}");
}
