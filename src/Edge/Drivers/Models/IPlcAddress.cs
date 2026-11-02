namespace IndustrialIot.Edge.Drivers.Models;

/// <summary>A validated tag address owned by one protocol; the catalog and command path only see this surface.</summary>
public interface IPlcAddress
{
    /// <summary>Normalized spelling of the address (used in telemetry, logs and messages).</summary>
    string Canonical { get; }

    /// <summary>Whether the address names a single bit (only <see cref="PlcDataType.Bool"/> fits it).</summary>
    bool IsBit { get; }

    /// <summary>Why a tag at this address can never be written; <c>null</c> when the area can be written.</summary>
    string? ReadOnlyReason { get; }
}

/// <summary>Parses and type-checks tag addresses for one protocol.</summary>
public interface IPlcAddressSyntax
{
    /// <summary>
    /// Parses <paramref name="address"/> and checks it can hold <paramref name="type"/>.
    /// Throws a <see cref="PlcException"/> subtype with a user-facing message when it cannot.
    /// </summary>
    IPlcAddress Parse(string address, PlcDataType type);

    /// <summary>
    /// As <see cref="Parse(string, PlcDataType)"/> for a tag with a <paramref name="length"/> (String characters); a
    /// protocol without String tags refuses any length.
    /// </summary>
    IPlcAddress Parse(string address, PlcDataType type, int? length) => length is null
        ? Parse(address, type)
        : throw new PlcValueException($"{type} tags with a Length are not supported by this protocol.");
}
