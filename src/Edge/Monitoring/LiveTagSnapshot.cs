using System.Collections.Immutable;
using System.Text.Json;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Edge.Monitoring;

/// <summary>One tag as the last poll cycle saw it; <see cref="Error"/> explains a bad reading.</summary>
public sealed record TagReading(string TagId, JsonElement? Value, TelemetryQuality Quality, DateTimeOffset TimestampUtc, string? Error);

/// <summary>
/// Result of one whole poll cycle of one device; <see cref="ReadDuration"/> is how long the batch read of its tags took
/// and <see cref="Requests"/> how many requests it sent (null when the driver does not say).
/// </summary>
public sealed record PollCycle(
    DateTimeOffset CompletedUtc,
    bool PlcUnavailable,
    IReadOnlyList<TagReading> Readings,
    TimeSpan ReadDuration,
    int? Requests = null);

/// <summary>
/// The latest poll cycle of every device, kept in memory for the local monitor page. Each device's loop replaces its
/// own entry after every cycle, so readers never trigger PLC I/O of their own.
/// </summary>
public sealed class LiveTagSnapshot
{
    private ImmutableDictionary<string, PollCycle> _devices = ImmutableDictionary.Create<string, PollCycle>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The latest cycle per device name; a device appears after its first cycle.</summary>
    public IReadOnlyDictionary<string, PollCycle> Devices => Volatile.Read(ref _devices);

    /// <summary>
    /// Null until a first cycle completed. With one device, its latest cycle; with several, every device's readings
    /// together (latest completion, any PLC unavailable, longest read).
    /// </summary>
    public PollCycle? Latest
    {
        get
        {
            var cycles = Volatile.Read(ref _devices).Values.ToList();
            return cycles switch
            {
                [] => null,
                [var only] => only,
                _ => new PollCycle(
                    cycles.Max(c => c.CompletedUtc),
                    cycles.Any(c => c.PlcUnavailable),
                    [.. cycles.SelectMany(c => c.Readings)],
                    cycles.Max(c => c.ReadDuration)),
            };
        }
    }

    /// <summary>
    /// When each device last completed a cycle with its PLC reachable. Kept apart from <see cref="Devices"/>, which an
    /// outage overwrites with the failed cycle.
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset> LastGoodUtc => Volatile.Read(ref _lastGood);

    public void Publish(string device, PollCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(cycle);
        if (!cycle.PlcUnavailable)
        {
            ImmutableInterlocked.Update(ref _lastGood, times => times.SetItem(device, cycle.CompletedUtc));
        }

        ImmutableInterlocked.Update(ref _devices, cycles => cycles.SetItem(device, cycle));
    }

    private ImmutableDictionary<string, DateTimeOffset> _lastGood = ImmutableDictionary.Create<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
}
