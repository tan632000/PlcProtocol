using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// Reads and writes one PLC. Failures surface as <see cref="PlcException"/> subtypes so callers can
/// decide between retrying (<see cref="PlcUnavailableException"/>) and reporting
/// (<see cref="PlcWriteOutcomeUnknownException"/>, <see cref="PlcAccessDeniedException"/>).
/// </summary>
public interface IPlcDriver
{
    /// <summary>
    /// Reads all <paramref name="tags"/> once; the result has one entry per tag in the same order, holding the CLR
    /// type mapped to the tag's data type or the exception for that tag. Cancellation throws.
    /// </summary>
    Task<IReadOnlyList<PlcReadResult>> ReadAsync(IReadOnlyList<ResolvedTag> tags, CancellationToken ct);

    /// <summary>
    /// Writes one value of the CLR type mapped to <paramref name="type"/>.
    /// <paramref name="ct"/> is honored only before the request is sent; once I/O starts the write
    /// runs to completion or fails, so a cancelled caller never leaves the outcome half-known.
    /// </summary>
    Task WriteAsync(IPlcAddress address, PlcDataType type, object value, CancellationToken ct);

    /// <summary>Returns whether a connection to the PLC can be established now.</summary>
    Task<bool> IsReachableAsync(CancellationToken ct);
}
