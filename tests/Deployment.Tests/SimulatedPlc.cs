using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Deployment.Tests;

/// <summary>Gives every device of the Edge a simulated PLC.</summary>
internal sealed class SimulatedPlcFactory : IPlcDriverFactory
{
    public IPlcDriver Create(ResolvedDevice device) => new SimulatedPlc();
}

/// <summary>A PLC whose every tag reads a changing Int.</summary>
internal sealed class SimulatedPlc : IPlcDriver
{
    private int _reads;

    public Task<IReadOnlyList<PlcReadResult>> ReadAsync(IReadOnlyList<ResolvedTag> tags, CancellationToken ct) =>
        SequentialBatchReader.ReadAsync(
            tags,
            (_, _) => Task.FromResult<object>((short)(Interlocked.Increment(ref _reads) % short.MaxValue)),
            ct);

    public Task WriteAsync(IPlcAddress address, PlcDataType type, object value, CancellationToken ct) => Task.CompletedTask;

    public Task<bool> IsReachableAsync(CancellationToken ct) => Task.FromResult(true);
}
