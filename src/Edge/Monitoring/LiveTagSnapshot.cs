using System.Text.Json;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Edge.Monitoring;

/// <summary>One tag as the last poll cycle saw it; <see cref="Error"/> explains a bad reading.</summary>
public sealed record TagReading(string TagId, JsonElement? Value, TelemetryQuality Quality, DateTimeOffset TimestampUtc, string? Error);

/// <summary>Result of one whole poll cycle.</summary>
public sealed record PollCycle(DateTimeOffset CompletedUtc, bool PlcUnavailable, IReadOnlyList<TagReading> Readings);

/// <summary>
/// The latest poll cycle, kept in memory for the local monitor page. The poller replaces it after every
/// cycle, so readers never trigger PLC I/O of their own.
/// </summary>
public sealed class LiveTagSnapshot
{
    private PollCycle? _latest;

    /// <summary>Null until the first cycle completed.</summary>
    public PollCycle? Latest => Volatile.Read(ref _latest);

    public void Publish(PollCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        Volatile.Write(ref _latest, cycle);
    }
}
