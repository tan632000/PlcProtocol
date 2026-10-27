using System.Diagnostics;
using IndustrialIot.Edge.Drivers.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using S7Net = S7.Net;

namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// <see cref="IPlcDriver"/> for S7-1200 over S7comm PUT/GET (S7netplus). It keeps one connection and runs one
/// operation at a time: S7netplus opens and closes sockets outside its internal queue, and an S7-1200 has
/// few connection slots. Any failure after the lock is taken discards the connection, so the next call
/// starts from a fresh socket instead of reading a stale response left on the old one. The one exception is a
/// complete PLC error reply inside a batch read (see <see cref="ReadAsync(IReadOnlyList{ResolvedTag}, CancellationToken)"/>):
/// the reply was read in full, so the socket holds nothing stale.
/// </summary>
public sealed class SiemensS7Driver : IPlcDriver, IAsyncDisposable
{
    private const string AccessDeniedHint =
        "In TIA Portal enable \"Permit access with PUT/GET communication\" and disable \"Optimized block access\" on the DB.";

    private readonly SiemensS7Options _options;
    private readonly ILogger<SiemensS7Driver> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Only the lock holder reads or replaces these.
    private S7Net.Plc? _plc;
    private int _activeOperations;

    // Canonical addresses of batch tags the PLC refused when read alone; they are read alone until that succeeds.
    // Only the lock holder reads or changes it.
    private readonly HashSet<string> _isolated = new(StringComparer.Ordinal);

    private int _maxObservedConcurrency;
    private int _lastRequestCount = -1;
    private volatile bool _disposed;

    public SiemensS7Driver(IOptions<SiemensS7Options> options, ILogger<SiemensS7Driver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? NullLogger<SiemensS7Driver>.Instance;
    }

    /// <summary>Highest number of operations seen inside the lock at once; must stay 1.</summary>
    internal int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    private TimeSpan DefaultTimeout => TimeSpan.FromMilliseconds(_options.TimeoutMs);

    /// <summary>
    /// Reads the batch with as few multi-variable requests as the negotiated PDU allows (<see cref="S7ReadPlanner"/>),
    /// taking the lock per request so writes can interleave. The rules of <see cref="SequentialBatchReader"/> hold:
    /// an invalid tag keeps its own error and is never sent; after a connection failure no further request is sent
    /// and every remaining tag gets that same exception; cancellation propagates. A request the PLC answers with an
    /// error (a refused item, or the header-only reply of PUT/GET off) keeps the connection, and its tags are read
    /// again one by one to find the refused ones; those are read alone in later cycles until a solo read succeeds.
    /// </summary>
    public async Task<IReadOnlyList<PlcReadResult>> ReadAsync(IReadOnlyList<ResolvedTag> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var results = new PlcReadResult?[tags.Count];
        var valid = new List<S7Item>(tags.Count);
        for (var i = 0; i < tags.Count; i++)
        {
            try
            {
                var address = RequireS7(tags[i].Address);
                PlcValueConverter.EnsureCompatible(address, tags[i].DataType);
                valid.Add(new S7Item(i, address, tags[i].DataType));
            }
            catch (Exception ex)
            {
                results[i] = PlcReadResult.Fail(ex);
            }
        }

        var requests = 0;
        PlcUnavailableException? unavailable = null;
        try
        {
            for (var next = 0; next < valid.Count && unavailable is null;)
            {
                var step = await ReadPackedAsync(valid, next, prune: next == 0, ct).ConfigureAwait(false);
                next = step.Next;
                if (step.Packed.Count > 0)
                {
                    requests++;
                }

                if (step.Values is not null)
                {
                    for (var k = 0; k < step.Packed.Count; k++)
                    {
                        results[step.Packed[k].Index] = PlcReadResult.Ok(step.Values[k]);
                    }
                }

                // Right away, so a later outage cannot cost the values of tags that were only waiting to be isolated.
                List<S7Item> solo = [.. step.Remembered, .. step.Values is null ? step.Packed : []];
                solo.Sort((a, b) => a.Index.CompareTo(b.Index));
                var soloResults = await SequentialBatchReader.ReadAsync(
                    [.. solo.Select(item => tags[item.Index])],
                    (tag, token) =>
                    {
                        requests++;
                        return ReadOneAsync(tag.Address, tag.DataType, DefaultTimeout, isolating: true, token);
                    },
                    ct).ConfigureAwait(false);
                for (var k = 0; k < solo.Count; k++)
                {
                    results[solo[k].Index] = soloResults[k];
                    unavailable ??= soloResults[k].Error as PlcUnavailableException;
                }
            }
        }
        catch (PlcUnavailableException ex)
        {
            unavailable = ex;
        }

        if (unavailable is not null)
        {
            for (var i = 0; i < results.Length; i++)
            {
                results[i] ??= PlcReadResult.Fail(unavailable);
            }
        }

        LogRequestCount(tags.Count, requests);
        return [.. results.Select(result => result!.Value)];
    }

    public Task<object> ReadAsync(IPlcAddress address, PlcDataType type, CancellationToken ct) =>
        ReadAsync(address, type, DefaultTimeout, ct);

    public Task WriteAsync(IPlcAddress address, PlcDataType type, object value, CancellationToken ct) =>
        WriteAsync(address, type, value, DefaultTimeout, ct);

    /// <summary>Rejects an address that belongs to another protocol, before any I/O.</summary>
    private static PlcAddress RequireS7(IPlcAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address as PlcAddress
            ?? throw new PlcAccessDeniedException($"Address '{address.Canonical}' is not an S7 address.");
    }

    /// <summary>Overload with an explicit budget so tests can make one caller give up sooner than another.</summary>
    internal Task<object> ReadAsync(IPlcAddress plcAddress, PlcDataType type, TimeSpan timeout, CancellationToken ct) =>
        ReadOneAsync(plcAddress, type, timeout, isolating: false, ct);

    /// <summary>
    /// One tag, one request. When <paramref name="isolating"/> (a batch tag read alone), a complete PLC error reply
    /// keeps the connection and remembers the tag, and a successful read forgets it; otherwise every failure
    /// discards the connection.
    /// </summary>
    private async Task<object> ReadOneAsync(IPlcAddress plcAddress, PlcDataType type, TimeSpan timeout, bool isolating, CancellationToken ct)
    {
        var address = RequireS7(plcAddress);
        PlcValueConverter.EnsureCompatible(address, type);

        using var budget = new CancellationTokenSource(timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await AcquireAsync(timeout, operation.Token, ct).ConfigureAwait(false);
        try
        {
            var plc = await ConnectAsync(operation.Token, ct).ConfigureAwait(false);
            var area = ToS7Area(address.Area);
            byte[] bytes;
            try
            {
                bytes = await plc.ReadBytesAsync(
                    area, address.DbNumber, address.ByteOffset, Math.Max(address.SizeBytes, 1), operation.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Token state decides, not the exception type: a cancelled read can surface as
                // ObjectDisposedException or IOException because S7netplus closes the socket on cancel.
                var plcError = !ct.IsCancellationRequested && IsPlcErrorResponse(ex);
                if (!(isolating && plcError))
                {
                    Discard();
                }

                ct.ThrowIfCancellationRequested();
                if (plcError)
                {
                    if (isolating)
                    {
                        _isolated.Add(address.Canonical);
                    }

                    throw new PlcAccessDeniedException($"PLC refused reading {address}. {AccessDeniedHint}", ex);
                }

                throw new PlcUnavailableException($"Reading {address} failed: {ex.Message}", ex);
            }

            if (isolating)
            {
                _isolated.Remove(address.Canonical);
            }

            return Decode(address, type, bytes);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// One multi-variable request, planned inside the lock from <paramref name="start"/> for the PDU of the current
    /// connection; remembered tags met on the way are handed back to be read alone. <see cref="PackedStep.Values"/>
    /// is <c>null</c> when the PLC answered the request with an error.
    /// </summary>
    private async Task<PackedStep> ReadPackedAsync(List<S7Item> items, int start, bool prune, CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(DefaultTimeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await AcquireAsync(DefaultTimeout, operation.Token, ct).ConfigureAwait(false);
        try
        {
            var plc = await ConnectAsync(operation.Token, ct).ConfigureAwait(false);
            if (prune)
            {
                _isolated.IntersectWith(items.Select(item => item.Address.Canonical));
            }

            var positions = new List<int>();
            for (var p = start; p < items.Count && positions.Count < S7ReadPlanner.MaxItems; p++)
            {
                if (!_isolated.Contains(items[p].Address.Canonical))
                {
                    positions.Add(p);
                }
            }

            if (positions.Count == 0)
            {
                return new PackedStep(items.Count, [], items[start..], null);
            }

            var count = S7ReadPlanner.Fit([.. positions.Select(p => ByteLength(items[p].Address))], 0, plc.MaxPDUSize);
            var end = positions[count - 1] + 1;
            S7Item[] packed = [.. positions.Take(count).Select(p => items[p])];
            S7Item[] remembered = [.. items[start..end].Where(item => _isolated.Contains(item.Address.Canonical))];

            // Raw bytes per tag (VarType.Byte), converted below exactly as a single-tag read converts them.
            var dataItems = packed.Select(item => new S7Net.Types.DataItem
            {
                DataType = ToS7Area(item.Address.Area),
                DB = item.Address.DbNumber,
                StartByteAdr = item.Address.ByteOffset,
                VarType = S7Net.VarType.Byte,
                Count = ByteLength(item.Address),
            }).ToList();

            try
            {
                await plc.ReadMultipleVarsAsync(dataItems, operation.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested && IsLibraryLimitError(ex))
                {
                    // Nothing was sent: the planner and the library disagree about the limits.
                    throw new InvalidOperationException($"S7 read request planned beyond the library limits: {ex.Message}", ex);
                }

                if (!ct.IsCancellationRequested && IsCompleteReadErrorReply(ex))
                {
                    return new PackedStep(end, packed, remembered, null);
                }

                Discard();
                ct.ThrowIfCancellationRequested();
                throw new PlcUnavailableException($"Reading {packed[0].Address} failed: {ex.Message}", ex);
            }

            var values = new object[packed.Length];
            for (var k = 0; k < packed.Length; k++)
            {
                // S7netplus returns a single byte as byte and takes item lengths from the request, so a short
                // reply shows up only as a missing or short value here.
                var bytes = dataItems[k].Value switch
                {
                    byte single => [single],
                    byte[] array => array,
                    _ => null,
                };
                var expected = ByteLength(packed[k].Address);
                if (bytes is null || bytes.Length != expected)
                {
                    Discard();
                    throw new PlcUnavailableException(
                        $"Reading {packed[0].Address} failed: the PLC returned {bytes?.Length ?? 0} byte(s) for {packed[k].Address}, expected {expected}.");
                }

                values[k] = Decode(packed[k].Address, packed[k].Type, bytes);
            }

            return new PackedStep(end, packed, remembered, values);
        }
        finally
        {
            Release();
        }
    }

    private void LogRequestCount(int tagCount, int requests)
    {
        // Information when the count changes (first batch, a refused tag, a new PDU), Debug otherwise.
        var level = Interlocked.Exchange(ref _lastRequestCount, requests) == requests ? LogLevel.Debug : LogLevel.Information;
        _logger.Log(level, "Read {TagCount} tag(s) in {RequestCount} request(s)", tagCount, requests);
    }

    private static int ByteLength(PlcAddress address) => Math.Max(address.SizeBytes, 1);

    private static object Decode(PlcAddress address, PlcDataType type, byte[] bytes) =>
        type == PlcDataType.Bool
            ? ((bytes[0] >> address.BitOffset!.Value) & 1) == 1
            : PlcValueConverter.FromBytes(type, bytes);

    /// <summary>Overload with an explicit budget so tests can make one caller give up sooner than another.</summary>
    internal async Task WriteAsync(IPlcAddress plcAddress, PlcDataType type, object value, TimeSpan timeout, CancellationToken ct)
    {
        var address = RequireS7(plcAddress);
        PlcValueConverter.EnsureCompatible(address, type);
        var bytes = PlcValueConverter.ToBytes(type, value);

        using var budget = new CancellationTokenSource(timeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        await AcquireAsync(timeout, operation.Token, ct).ConfigureAwait(false);
        try
        {
            var plc = await ConnectAsync(operation.Token, ct).ConfigureAwait(false);
            var area = ToS7Area(address.Area);
            try
            {
                // Only the budget applies from here on. Cancelling a write that may already be on the wire would
                // tell the caller "nothing happened" when the PLC may have applied the value.
                if (type == PlcDataType.Bool)
                {
                    await plc.WriteBitAsync(
                        area, address.DbNumber, address.ByteOffset, address.BitOffset!.Value, (bool)value, budget.Token).ConfigureAwait(false);
                }
                else
                {
                    await plc.WriteBytesAsync(area, address.DbNumber, address.ByteOffset, bytes, budget.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Discard();
                if (IsPlcErrorResponse(ex))
                {
                    throw new PlcAccessDeniedException($"PLC refused writing {address}; the value was not applied. {AccessDeniedHint}", ex);
                }

                throw new PlcWriteOutcomeUnknownException(
                    $"Writing {address} was not acknowledged; the PLC may or may not have applied it: {ex.Message}", ex);
            }
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Returns <c>false</c> (never throws) when the PLC is unreachable or the driver is busy.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(DefaultTimeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            await AcquireAsync(DefaultTimeout, operation.Token, ct).ConfigureAwait(false);
        }
        catch (PlcBusyException)
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
            Release();
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

    private async Task AcquireAsync(TimeSpan timeout, CancellationToken operationToken, CancellationToken callerToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await _lock.WaitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            callerToken.ThrowIfCancellationRequested();

            // Nothing was sent and the holder's connection is not ours to touch.
            throw new PlcBusyException($"Timed out after {timeout.TotalMilliseconds} ms waiting for another PLC operation to finish.");
        }

        if (_disposed)
        {
            _lock.Release();
            throw new ObjectDisposedException(nameof(SiemensS7Driver));
        }

        var active = Interlocked.Increment(ref _activeOperations);
        int observed;
        while (active > (observed = Volatile.Read(ref _maxObservedConcurrency)))
        {
            Interlocked.CompareExchange(ref _maxObservedConcurrency, active, observed);
        }
    }

    private void Release()
    {
        Interlocked.Decrement(ref _activeOperations);
        _lock.Release();
    }

    /// <summary>Builds the unopened S7netplus client for the configured CPU type (no network I/O).</summary>
    internal S7Net.Plc CreatePlc()
    {
        if (!SiemensS7Options.TryParseCpuType(_options.CpuType, out var cpuType))
        {
            throw new InvalidOperationException($"{SiemensS7Options.SectionName}:CpuType must be S71200 or S71500 (was '{_options.CpuType}').");
        }

        return new S7Net.Plc(cpuType, _options.Host, _options.Port, (short)_options.Rack, (short)_options.Slot)
        {
            ReadTimeout = _options.TimeoutMs,
            WriteTimeout = _options.TimeoutMs,
        };
    }

    /// <summary>Returns the open connection or opens a new one. Caller must hold the lock.</summary>
    private async Task<S7Net.Plc> ConnectAsync(CancellationToken operationToken, CancellationToken callerToken)
    {
        if (_plc is { IsConnected: true } connected)
        {
            return connected;
        }

        Discard();
        var plc = CreatePlc();

        // Stored before opening so a disposal or discard can still close a handshake that hangs.
        _plc = plc;
        try
        {
            await plc.OpenAsync(operationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Discard();
            callerToken.ThrowIfCancellationRequested();
            throw new PlcUnavailableException($"Cannot connect to PLC {_options.Host}:{_options.Port}: {ex.Message}", ex);
        }

        if (operationToken.IsCancellationRequested)
        {
            // The budget ran out just as the handshake finished. No request has been sent, so this is still
            // "unavailable" rather than starting I/O with no time left.
            callerToken.ThrowIfCancellationRequested();
            throw new PlcUnavailableException($"Connecting to PLC {_options.Host}:{_options.Port} used up the whole timeout.");
        }

        _logger.LogInformation("Connected to PLC {Host}:{Port}, PDU size {PduSize}", _options.Host, _options.Port, plc.MaxPDUSize);
        return plc;
    }

    private void Discard()
    {
        var plc = _plc;
        _plc = null;
        plc?.Close();
    }

    /// <summary>
    /// True when the PLC answered with an error instead of data. S7netplus 0.20.0 reports bad item return
    /// codes as "Received error from PLC: ..." / "Invalid response from PLC: ...", but it does not decode the
    /// S7 header error class at all: a header-only error reply (e.g. 0x8104 when PUT/GET is disabled) is
    /// too short to hold an item code and surfaces as a byte-count error on reads and as an
    /// <see cref="IndexOutOfRangeException"/> wrapped in a write error on writes. Both mean a complete reply
    /// arrived, so the outcome is known.
    /// </summary>
    private static bool IsPlcErrorResponse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("Received error from PLC", StringComparison.Ordinal)
                || current.Message.Contains("Invalid response from PLC", StringComparison.Ordinal))
            {
                return true;
            }

            if (current is S7Net.PlcException { ErrorCode: S7Net.ErrorCode.WrongNumberReceivedBytes })
            {
                return true;
            }

            // Only an IndexOutOfRangeException raised by the write method itself (reading item code s7data[14])
            // means "short but complete reply". The same exception type thrown while parsing a truncated
            // TPKT/COTP frame (COTP.TPDU constructor) says nothing about the outcome, so it must stay unknown.
            if (current is S7Net.PlcException { ErrorCode: S7Net.ErrorCode.WriteData, InnerException: IndexOutOfRangeException inner }
                && IsThrownBy(inner, "WriteBytesWithASingleRequestAsync", "WriteBitWithASingleRequestAsync"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <see cref="IsPlcErrorResponse"/> for a multi-variable read. S7netplus reads the first item's return code at
    /// s7data[14] inside <c>ReadMultipleVarsAsync</c>, so the header-only 0x8104 reply of PUT/GET off surfaces there as
    /// an <see cref="IndexOutOfRangeException"/>; the same exception thrown while parsing a truncated frame (COTP)
    /// or a reply that ends early says nothing about the connection and stays a connection failure.
    /// </summary>
    private static bool IsCompleteReadErrorReply(Exception exception) =>
        IsPlcErrorResponse(exception)
        || (exception is S7Net.PlcException { ErrorCode: S7Net.ErrorCode.ReadData, InnerException: IndexOutOfRangeException inner }
            && IsThrownBy(inner, "ReadMultipleVarsAsync"));

    /// <summary>The plain exception S7netplus 0.20.0 throws before sending a read that exceeds the PDU.</summary>
    private static bool IsLibraryLimitError(Exception exception) =>
        exception.GetType() == typeof(Exception)
        && (exception.Message.StartsWith("Too many vars requested for read", StringComparison.Ordinal)
            || exception.Message.StartsWith("Too much data requested for read", StringComparison.Ordinal));

    /// <summary>
    /// Whether the innermost S7netplus frame of the stack trace belongs to one of <paramref name="methods"/>.
    /// <see cref="Exception.TargetSite"/> cannot be used: the runtime raises array bound failures from its own throw
    /// helper. Async methods run inside a compiler-generated state machine named after the method, e.g.
    /// "&lt;WriteBytesWithASingleRequestAsync&gt;d__56". If the frame cannot be resolved, the answer is false, which
    /// keeps the outcome unknown. Tied to S7netplus 0.20.0; re-check on upgrade.
    /// </summary>
    private static bool IsThrownBy(Exception exception, params string[] methods)
    {
        var s7Assembly = typeof(S7Net.Plc).Assembly;
        var origin = new StackTrace(exception, fNeedFileInfo: false).GetFrames()
            .Select(frame => frame.GetMethod()?.DeclaringType)
            .FirstOrDefault(type => type?.Assembly == s7Assembly);

        return origin?.Name is { } name
            && methods.Any(method => name.Contains($"<{method}>", StringComparison.Ordinal));
    }

    private readonly record struct S7Item(int Index, PlcAddress Address, PlcDataType Type);

    /// <summary>Result of one planned request: where the next one starts, what it read, and what to read alone.</summary>
    private sealed record PackedStep(int Next, IReadOnlyList<S7Item> Packed, IReadOnlyList<S7Item> Remembered, object[]? Values);

    private static S7Net.DataType ToS7Area(PlcArea area) => area switch
    {
        PlcArea.DataBlock => S7Net.DataType.DataBlock,
        PlcArea.Memory => S7Net.DataType.Memory,
        PlcArea.Input => S7Net.DataType.Input,
        PlcArea.Output => S7Net.DataType.Output,
        _ => throw new PlcValueException($"Unsupported area '{area}'."),
    };
}
