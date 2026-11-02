using System.Net.Sockets;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Slmp;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Drivers.Mc1e;

/// <summary>Connection settings for one FX3U Ethernet adapter (MC protocol, A-compatible 1E frame, binary).</summary>
public sealed class Mc1eOptions
{
    public string Host { get; set; } = "";

    /// <summary>The MC protocol port set in GX Works2 for the Ethernet adapter. Required: there is no default.</summary>
    public int Port { get; set; }

    /// <summary>Budget for one request: lock wait + connect + request/response.</summary>
    public int TimeoutMs { get; set; } = 3000;
}

/// <summary>Rejects invalid <see cref="Mc1eOptions"/> so the host fails at startup, not at the first PLC call.</summary>
internal sealed class Mc1eOptionsValidator : IValidateOptions<Mc1eOptions>
{
    private const int S7Port = 102;

    public ValidateOptionsResult Validate(string? name, Mc1eOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            failures.Add("Host is required.");
        }

        if (options.Port is < 1 or > 65535 || options.Port == S7Port)
        {
            failures.Add($"Port must be the MC protocol port set in GX Works2, within 1..65535 and not {S7Port} (was {options.Port}).");
        }

        if (options.TimeoutMs is < 1000 or > 60000)
        {
            failures.Add($"TimeoutMs must be within 1000..60000 (was {options.TimeoutMs}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Reads one FX3U through its Ethernet adapter with the MC protocol (A-compatible 1E frame, binary; FX3U-ENET-ADP
/// User's Manual JY997D45801). One connection and one request at a time; tags are read in word units, bit devices as
/// 16-point words. A refused block whose complete code names a device or range is read again tag by tag to find the
/// refused tags, which are then read alone; a lost connection stops the batch. Writes go to D in word units and to M
/// and Y in bit units; X and anything that does not fit are refused before any I/O.
/// </summary>
public sealed class MitsubishiMc1eDriver : IPlcDriver, IPlcReadStatistics, IAsyncDisposable
{
    // Complete codes naming the device or range (section 10.3.2): wrong device (56H), points or range beyond the limit
    // (57H), head device out of range (58H). Only these justify reading the block's tags one by one.
    private static readonly byte[] AddressCompleteCodes = [0x56, 0x57, 0x58];

    private const string BinaryHint =
        "In GX Works2 the Ethernet adapter's communication data code must be \"Binary\" and the port must use the MC protocol.";

    private readonly Mc1eOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Only the lock holder reads or replaces these. The stream owns the socket and is used for reading only.
    private Socket? _socket;
    private NetworkStream? _stream;
    private volatile bool _disposed;

    // Tags the adapter refused with an address complete code; touched only by ReadAsync (one poller cycle at a time).
    private readonly HashSet<string> _badTags = new(StringComparer.Ordinal);

    private int _batchRequests;
    private int _lastBatchRequests = -1;

    public MitsubishiMc1eDriver(IOptions<Mc1eOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(_options.TimeoutMs);

    public int LastBatchRequests => Volatile.Read(ref _lastBatchRequests);

    public async Task<IReadOnlyList<PlcReadResult>> ReadAsync(IReadOnlyList<ResolvedTag> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var results = new PlcReadResult[tags.Count];
        var slots = new List<Mc1eTagSlot>(tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            if (tags[i].Address is Mc1eAddress address)
            {
                slots.Add(new Mc1eTagSlot(i, tags[i], address, address.IsBit ? 1 : SlmpBlockPlanner.WordCount(tags[i].DataType, tags[i].Length)));
            }
            else
            {
                results[i] = PlcReadResult.Fail(new PlcAccessDeniedException($"Address '{tags[i].Address.Canonical}' is not an FX3U (MC 1E) address."));
            }
        }

        Volatile.Write(ref _batchRequests, 0);
        var bad = slots.Where(s => _badTags.Contains(s.Tag.TagId)).ToList();
        PlcUnavailableException? unavailable = null;
        foreach (var block in Mc1eBlockPlanner.Plan(slots.Where(s => !_badTags.Contains(s.Tag.TagId))))
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

            unavailable = await ReadSlotsAloneAsync([slot], results, ct).ConfigureAwait(false);
        }

        Volatile.Write(ref _lastBatchRequests, Volatile.Read(ref _batchRequests));
        return results;
    }

    /// <summary>
    /// Writes one value with one batch write: D in word units (the value's words, low word first), M and Y one point in
    /// bit units. <paramref name="ct"/> is honored only until the request is sent; afterwards a failure means the outcome
    /// is unknown. Whether an FX3U may be written at all is decided at start (the model's write guard).
    /// </summary>
    public async Task WriteAsync(IPlcAddress address, PlcDataType type, object value, CancellationToken ct)
    {
        var (request, target) = BuildWrite(address, type, value);

        using var budget = new CancellationTokenSource(Timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await AcquireAsync(operation.Token, ct).ConfigureAwait(false);
        try
        {
            var (socket, stream) = await ConnectAsync(operation.Token, ct).ConfigureAwait(false);
            byte completeCode;
            byte abnormalCode = 0;
            try
            {
                // Only the budget applies from here on: cancelling a write that may already be on the wire would tell
                // the caller "nothing happened" when the PLC may have applied the value.
                await SendAllAsync(socket, request, budget.Token).ConfigureAwait(false);
                var header = new byte[Mc1eFrame.ReplyHeaderLength];
                await stream.ReadExactlyAsync(header, budget.Token).ConfigureAwait(false);
                if (header[0] != Mc1eFrame.ReplySubheader(request[0]))
                {
                    throw Protocol($"reply to writing {target} starts with 0x{header[0]:X2}, not 0x{Mc1eFrame.ReplySubheader(request[0]):X2}. {BinaryHint}");
                }

                completeCode = header[1];
                if (completeCode == Mc1eFrame.PlcCannotProcess)
                {
                    var abnormal = new byte[2];
                    await stream.ReadExactlyAsync(abnormal, budget.Token).ConfigureAwait(false);
                    abnormalCode = abnormal[0];
                    if (abnormal[1] != 0x00)
                    {
                        throw Protocol($"reply to writing {target} has complete code 5BH but no 00H after its abnormal code.");
                    }
                }
            }
            catch (Exception ex)
            {
                Discard();
                throw new PlcWriteOutcomeUnknownException(
                    $"Writing {target} was not acknowledged; the PLC may or may not have applied it: {ex.Message}", ex);
            }

            if (completeCode != 0)
            {
                var abnormal = completeCode == Mc1eFrame.PlcCannotProcess ? $" (abnormal code 0x{abnormalCode:X2})" : "";
                throw new PlcAccessDeniedException($"MC 1E complete code 0x{completeCode:X2}{abnormal} writing {target}; the value was not applied.");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    internal byte[] BuildBatchWriteWords(Mc1eDevice device, int head, IReadOnlyList<ushort> words) =>
        Mc1eFrame.BatchWriteWords(device, head, words, Mc1eFrame.MonitoringTimer(_options.TimeoutMs));

    internal byte[] BuildBatchWriteBits(Mc1eDevice device, int head, IReadOnlyList<bool> points) =>
        Mc1eFrame.BatchWriteBits(device, head, points, Mc1eFrame.MonitoringTimer(_options.TimeoutMs));

    /// <summary>The batch write for one tag, or the refusal (thrown before any I/O).</summary>
    private (byte[] Request, Mc1eAddress Target) BuildWrite(IPlcAddress address, PlcDataType type, object value)
    {
        if (address is not Mc1eAddress mc)
        {
            throw new PlcAccessDeniedException($"Address '{address?.Canonical}' is not an FX3U (MC 1E) address.");
        }

        if (mc.ReadOnlyReason is { } reason)
        {
            throw new PlcAccessDeniedException(reason);
        }

        if (mc.IsBit)
        {
            if (type != PlcDataType.Bool || value is not bool on)
            {
                throw new PlcValueException($"Address '{mc}' holds Bool; a {type} value cannot be written there.");
            }

            return (BuildBatchWriteBits(mc.Device, mc.Number, [on]), mc);
        }

        var words = PlcValueConverter.ToWords(type, value);
        if (words.Length > Mc1eFrame.MaxWriteWords || mc.Number + words.Length - 1 > Mc1eAddressSyntax.MaxD)
        {
            throw new PlcValueException($"Writing {words.Length} word(s) at {mc} does not fit D0-D{Mc1eAddressSyntax.MaxD} in one batch write.");
        }

        return (BuildBatchWriteWords(mc.Device, mc.Number, words), mc);
    }

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

    internal byte[] BuildBatchReadWords(Mc1eDevice device, int headDevice, int points) =>
        Mc1eFrame.BatchReadWords(device, headDevice, points, Mc1eFrame.MonitoringTimer(_options.TimeoutMs));

    private async Task<PlcUnavailableException?> ReadBlockAsync(Mc1eBlock block, PlcReadResult[] results, CancellationToken ct)
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

        if (reply.CompleteCode == 0)
        {
            foreach (var slot in block.Slots)
            {
                Complete(slot, block.Head, reply.Words, results);
            }

            return null;
        }

        var isAddressError = AddressCompleteCodes.Contains(reply.CompleteCode);
        if (!isAddressError || block.Slots.Count == 1)
        {
            FailAll(block.Slots, results, new PlcAccessDeniedException(DeniedMessage(reply, block.Device, block.Head, block.Count)));
            if (isAddressError)
            {
                _badTags.Add(block.Slots[0].Tag.TagId);
            }

            return null;
        }

        return await ReadSlotsAloneAsync(block.Slots, results, ct).ConfigureAwait(false);
    }

    private async Task<PlcUnavailableException?> ReadSlotsAloneAsync(IReadOnlyList<Mc1eTagSlot> slots, PlcReadResult[] results, CancellationToken ct)
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
                var reply = await ReadWordsAsync(slot.Address.Device, slot.FirstWord, slot.Words, ct).ConfigureAwait(false);
                if (reply.CompleteCode == 0)
                {
                    _badTags.Remove(slot.Tag.TagId);
                    Complete(slot, slot.FirstWord, reply.Words, results);
                    continue;
                }

                if (AddressCompleteCodes.Contains(reply.CompleteCode))
                {
                    _badTags.Add(slot.Tag.TagId);
                }

                results[slot.Index] = PlcReadResult.Fail(
                    new PlcAccessDeniedException(DeniedMessage(reply, slot.Address.Device, slot.FirstWord, slot.Words)));
            }
            catch (PlcUnavailableException ex)
            {
                unavailable = ex;
                results[slot.Index] = PlcReadResult.Fail(ex);
            }
        }

        return unavailable;
    }

    private static void Complete(Mc1eTagSlot slot, int firstWord, ushort[] words, PlcReadResult[] results)
    {
        var own = words.AsSpan(slot.FirstWord - firstWord, slot.Words);
        results[slot.Index] = PlcReadResult.Ok(
            slot.Address.IsBit
                ? SlmpBlockPlanner.DecodeBit(slot.Address.Number, own[0])
                : SlmpBlockPlanner.Decode(slot.Tag.DataType, own, slot.Tag.Length));
    }

    private static void FailAll(IReadOnlyList<Mc1eTagSlot> slots, PlcReadResult[] results, Exception error)
    {
        foreach (var slot in slots)
        {
            results[slot.Index] = PlcReadResult.Fail(error);
        }
    }

    /// <summary>Device range of <paramref name="count"/> words from word <paramref name="word"/>, as GX Works2 spells it.</summary>
    private static string Range(Mc1eDevice device, int word, int count)
    {
        var bit = Mc1eFrame.IsBitDevice(device);
        var first = bit ? word * 16 : word;
        var last = (bit ? (word + count) * 16 : word + count) - 1;
        return $"{Mc1eAddress.Spell(device, first)}–{Mc1eAddress.Spell(device, last)}";
    }

    private static string DeniedMessage(WordsReply reply, Mc1eDevice device, int word, int count)
    {
        var hint = reply.CompleteCode switch
        {
            0x56 => " The device designation is wrong (manual section 10.3.2).",
            0x57 => " The points or the device range exceed what the adapter allows (manual section 10.3.2).",
            0x58 => " The head device is outside the allowed range (manual section 10.3.2).",
            Mc1eFrame.PlcCannotProcess => $" The PLC cannot process requests now (abnormal code 0x{reply.AbnormalCode:X2}, manual section 10.3.2).",
            0x60 => " The PLC did not answer the adapter within the monitoring time (manual section 10.3.2).",
            _ => "",
        };
        return $"MC 1E complete code 0x{reply.CompleteCode:X2} reading {Range(device, word, count)}.{hint}";
    }

    private readonly record struct WordsReply(byte CompleteCode, byte AbnormalCode, ushort[] Words);

    /// <summary>One batch read in word units. An error complete code is a normal reply here; a lost or invalid reply throws.</summary>
    private async Task<WordsReply> ReadWordsAsync(Mc1eDevice device, int word, int count, CancellationToken ct)
    {
        var head = Mc1eFrame.IsBitDevice(device) ? word * 16 : word;
        var request = BuildBatchReadWords(device, head, count);
        var description = $"reading {count} word(s) from {Mc1eAddress.Spell(device, head)}";

        using var budget = new CancellationTokenSource(Timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await AcquireAsync(operation.Token, ct).ConfigureAwait(false);
        try
        {
            var (socket, stream) = await ConnectAsync(operation.Token, ct).ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _batchRequests);
                await SendAllAsync(socket, request, operation.Token).ConfigureAwait(false);

                // TCP may deliver the reply in pieces: read exactly the header, then exactly what it implies, so no byte
                // of this reply is left behind for the next request.
                var header = new byte[Mc1eFrame.ReplyHeaderLength];
                await stream.ReadExactlyAsync(header, operation.Token).ConfigureAwait(false);
                if (header[0] != Mc1eFrame.ReplySubheader(Mc1eCommands.BatchReadWords))
                {
                    throw Protocol($"reply to {description} starts with 0x{header[0]:X2}, not 0x81. {BinaryHint}");
                }

                var completeCode = header[1];
                if (completeCode == Mc1eFrame.PlcCannotProcess)
                {
                    var abnormal = new byte[2];
                    await stream.ReadExactlyAsync(abnormal, operation.Token).ConfigureAwait(false);
                    if (abnormal[1] != 0x00)
                    {
                        throw Protocol($"reply to {description} has complete code 5BH but no 00H after its abnormal code.");
                    }

                    return new WordsReply(completeCode, abnormal[0], []);
                }

                if (completeCode != 0)
                {
                    return new WordsReply(completeCode, 0, []);
                }

                var data = new byte[2 * count];
                await stream.ReadExactlyAsync(data, operation.Token).ConfigureAwait(false);
                return new WordsReply(0, 0, SlmpBlockPlanner.ToWords(data));
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
            throw new ObjectDisposedException(nameof(MitsubishiMc1eDriver));
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
            // WaitAsync bounds the connect even if the OS ignores the token (a SYN to a powered-off PLC can otherwise
            // hang for about 21 s on Windows).
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

    private static PlcUnavailableException Protocol(string message) => new($"Invalid MC 1E reply: {message}");
}
