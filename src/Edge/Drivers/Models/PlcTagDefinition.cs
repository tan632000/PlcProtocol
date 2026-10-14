namespace IndustrialIot.Edge.Drivers.Models;

/// <summary>
/// One configured PLC tag. Settable properties (instead of a positional record) let the configuration
/// binder fill it from <c>Edge:Tags</c>; <see cref="PlcTagCatalog"/> validates every field at startup.
/// </summary>
public sealed record PlcTagDefinition
{
    /// <summary>Id used by telemetry and commands; 1..64 chars <c>[A-Za-z0-9_.-]</c>, case-insensitive.</summary>
    public string TagId { get; init; } = "";

    /// <summary>S7 address string, e.g. <c>DB1.DBW2</c>; parsed when the catalog is built.</summary>
    public string Address { get; init; } = "";

    public PlcDataType DataType { get; init; }

    /// <summary>Commands may write this tag only when <c>true</c>. Defaults to read-only.</summary>
    public bool Writable { get; init; }

    /// <summary>Optional inclusive lower bound for writes; numeric types only.</summary>
    public decimal? Min { get; init; }

    /// <summary>Optional inclusive upper bound for writes; numeric types only.</summary>
    public decimal? Max { get; init; }
}
