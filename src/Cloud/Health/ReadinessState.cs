using System.Collections.Concurrent;

namespace IndustrialIot.Cloud.Health;

/// <summary>
/// Last known result of each readiness check. Written by background checks, read by <c>/health/ready</c>,
/// so a probe never causes database or broker I/O.
/// </summary>
public sealed class ReadinessState
{
    public const string Database = "database";

    // Not ready until the first database check has succeeded.
    private readonly ConcurrentDictionary<string, bool> _checks = new(StringComparer.Ordinal) { [Database] = false };

    public void Set(string check, bool healthy) => _checks[check] = healthy;

    /// <summary>Ready when every reported check is healthy.</summary>
    public bool IsReady => _checks.Values.All(healthy => healthy);

    public IReadOnlyDictionary<string, bool> Snapshot() => new Dictionary<string, bool>(_checks, StringComparer.Ordinal);
}
