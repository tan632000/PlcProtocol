using System.Collections.Concurrent;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IPlcDriver"/>: per-address read results or exceptions, per-address write
/// exceptions, a record of every call, and an optional gate that holds calls until released.
/// </summary>
internal sealed class FakePlcDriver : IPlcDriver
{
    private readonly ConcurrentDictionary<string, Func<object>> _reads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Exception> _writeFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(PlcAddress Address, PlcDataType Type)> _readCalls = new();
    private readonly ConcurrentQueue<(PlcAddress Address, PlcDataType Type, object Value)> _writeCalls = new();
    private volatile TaskCompletionSource? _gate;
    private TaskCompletionSource _gateEntered = NewSignal();

    public IReadOnlyList<(PlcAddress Address, PlcDataType Type)> Reads => [.. _readCalls];

    public IReadOnlyList<(PlcAddress Address, PlcDataType Type, object Value)> Writes => [.. _writeCalls];

    public void SetRead(string address, object value) => _reads[address] = () => value;

    public void SetReadFailure(string address, Exception exception) => _reads[address] = () => throw exception;

    public void SetWriteFailure(string address, Exception exception) => _writeFailures[address] = exception;

    /// <summary>From now on every call waits until <see cref="Release"/>.</summary>
    public void Block()
    {
        _gateEntered = NewSignal();
        _gate = NewSignal();
    }

    /// <summary>Completes when a call reaches the gate.</summary>
    public Task GateEntered => _gateEntered.Task;

    public void Release() => Interlocked.Exchange(ref _gate, null)?.TrySetResult();

    public async Task<object> ReadAsync(PlcAddress address, PlcDataType type, CancellationToken ct)
    {
        _readCalls.Enqueue((address, type));
        await PassGateAsync(ct).ConfigureAwait(false);

        return _reads.TryGetValue(address.Canonical, out var result) ? result() : DefaultValue(type);
    }

    public async Task WriteAsync(PlcAddress address, PlcDataType type, object value, CancellationToken ct)
    {
        _writeCalls.Enqueue((address, type, value));
        await PassGateAsync(ct).ConfigureAwait(false);

        if (_writeFailures.TryGetValue(address.Canonical, out var failure))
        {
            throw failure;
        }
    }

    public Task<bool> IsReachableAsync(CancellationToken ct) => Task.FromResult(true);

    private async Task PassGateAsync(CancellationToken ct)
    {
        if (_gate is { } gate)
        {
            _gateEntered.TrySetResult();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private static object DefaultValue(PlcDataType type) => type switch
    {
        PlcDataType.Bool => false,
        PlcDataType.Byte => (byte)0,
        PlcDataType.Word => (ushort)0,
        PlcDataType.Int => (short)0,
        PlcDataType.DWord => 0u,
        PlcDataType.DInt => 0,
        PlcDataType.Real => 0f,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
