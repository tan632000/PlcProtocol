using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Drivers;

/// <summary>S7 address spelling (<c>DB1.DBW0</c>, <c>M0.1</c>, ...) and the S7 size rule per data type.</summary>
public sealed class S7AddressSyntax : IPlcAddressSyntax
{
    /// <summary>
    /// The longest S7 String tag: a <c>STRING[218]</c> item is 220 bytes, so one item still fits a reply in the
    /// 240-byte PDU an S7-1200 negotiates.
    /// </summary>
    public const int MaxStringLength = 218;

    public IPlcAddress Parse(string address, PlcDataType type) => Parse(address, type, null);

    public IPlcAddress Parse(string address, PlcDataType type, int? length)
    {
        var parsed = PlcAddressParser.Parse(address);
        EnsureCompatible(parsed, type);
        if (type == PlcDataType.String && length is not (>= 1 and <= MaxStringLength))
        {
            var given = length?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing";
            throw new PlcValueException(
                $"S7 String Length must be 1..{MaxStringLength} (was {given}): one STRING item must fit a 240-byte PDU.");
        }

        return parsed;
    }

    /// <summary>
    /// Throws <see cref="PlcValueException"/> when the address cannot hold the type. String, LReal and DTL start at a
    /// byte address (<c>DB1.DBB10</c>, <c>MB4</c>); their size comes from the type.
    /// </summary>
    internal static void EnsureCompatible(PlcAddress address, PlcDataType type)
    {
        ArgumentNullException.ThrowIfNull(address);

        var byteAddressed = type is PlcDataType.String or PlcDataType.LReal or PlcDataType.DTL;
        var compatible = type == PlcDataType.Bool
            ? address.IsBit
            : !address.IsBit && address.SizeBytes == (byteAddressed ? 1 : PlcValueConverter.SizeOf(type));

        if (!compatible)
        {
            throw new PlcValueException(byteAddressed
                ? $"Address '{address}' cannot hold a {type} value; it starts at a byte address such as DB1.DBB10."
                : $"Address '{address}' cannot hold a {type} value.");
        }
    }
}
