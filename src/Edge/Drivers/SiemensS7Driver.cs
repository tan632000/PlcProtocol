using System.Diagnostics;
using IndustrialIot.Edge.Drivers.Models;
using Microsoft.Extensions.Options;
using S7Net = S7.Net;

namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// <see cref="IPlcDriver"/> for S7-1200 over S7comm PUT/GET (S7netplus). It keeps one connection and runs one
/// operation at a time: S7netplus opens and closes sockets outside its internal queue, and an S7-1200 has
/// few connection slots. Any failure after the lock is taken discards the connection, so the next call
/// starts from a fresh socket instead of reading a stale response left on the old one.
/// </summary>
public sealed class SiemensS7Driver : IPlcDriver, IAsyncDisposable
{
    private const string AccessDeniedHint =
        "In TIA Portal enable \"Permit access with PUT/GET communication\" and disable \"Optimized block access\" on the DB.";

    private readonly SiemensS7Options _options;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Only the lock holder reads or replaces these.
    private S7Net.Plc? _plc;
    private int _activeOperations;

    private int _maxObservedConcurrency;
    private volatile bool _disposed;

    public SiemensS7Driver(IOptions<SiemensS7Options> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>Highest number of operations seen inside the lock at once; must stay 1.</summary>
    internal int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    private TimeSpan DefaultTimeout => TimeSpan.FromMilliseconds(_options.TimeoutMs);

    /// <summary>One read per tag, each taking the lock on its own so writes can interleave between tags.</summary>
    public Task<IReadOnlyList<PlcReadResult>> ReadAsync(IReadOnlyList<ResolvedTag> tags, CancellationToken ct) =>
        SequentialBatchReader.ReadAsync(tags, (tag, token) => ReadAsync(tag.Address, tag.DataType, token), ct);

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
    internal async Task<object> ReadAsync(IPlcAddress plcAddress, PlcDataType type, TimeSpan timeout, CancellationToken ct)
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
                Discard();

                // Token state decides, not the exception type: a cancelled read can surface as
                // ObjectDisposedException or IOException because S7netplus closes the socket on cancel.
                ct.ThrowIfCancellationRequested();
                if (IsPlcErrorResponse(ex))
                {
                    throw new PlcAccessDeniedException($"PLC refused reading {address}. {AccessDeniedHint}", ex);
                }

                throw new PlcUnavailableException($"Reading {address} failed: {ex.Message}", ex);
            }

            return type == PlcDataType.Bool
                ? ((bytes[0] >> address.BitOffset!.Value) & 1) == 1
                : PlcValueConverter.FromBytes(type, bytes);
        }
        finally
        {
            Release();
        }
    }

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
                && IsThrownBySingleWriteRequest(inner))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the innermost S7netplus frame of the stack trace. <see cref="Exception.TargetSite"/> cannot be used:
    /// the runtime raises array bound failures from its own throw helper. Async methods run inside a
    /// compiler-generated state machine named after the method, e.g. "&lt;WriteBytesWithASingleRequestAsync&gt;d__56".
    /// If the frame cannot be resolved, the answer is false, which keeps the write outcome unknown.
    /// Tied to S7netplus 0.20.0; re-check on upgrade.
    /// </summary>
    private static bool IsThrownBySingleWriteRequest(Exception exception)
    {
        var s7Assembly = typeof(S7Net.Plc).Assembly;
        var origin = new StackTrace(exception, fNeedFileInfo: false).GetFrames()
            .Select(frame => frame.GetMethod()?.DeclaringType)
            .FirstOrDefault(type => type?.Assembly == s7Assembly);

        return origin?.Name is { } name
            && (name.Contains("WriteBytesWithASingleRequestAsync", StringComparison.Ordinal)
                || name.Contains("WriteBitWithASingleRequestAsync", StringComparison.Ordinal));
    }

    private static S7Net.DataType ToS7Area(PlcArea area) => area switch
    {
        PlcArea.DataBlock => S7Net.DataType.DataBlock,
        PlcArea.Memory => S7Net.DataType.Memory,
        PlcArea.Input => S7Net.DataType.Input,
        PlcArea.Output => S7Net.DataType.Output,
        _ => throw new PlcValueException($"Unsupported area '{area}'."),
    };
}
