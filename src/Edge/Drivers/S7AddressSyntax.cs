using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Drivers;

/// <summary>S7 address spelling (<c>DB1.DBW0</c>, <c>M0.1</c>, ...) and the S7 size rule per data type.</summary>
public sealed class S7AddressSyntax : IPlcAddressSyntax
{
    public IPlcAddress Parse(string address, PlcDataType type)
    {
        var parsed = PlcAddressParser.Parse(address);
        EnsureCompatible(parsed, type);
        return parsed;
    }

    /// <summary>Throws <see cref="PlcValueException"/> when the address size cannot hold the type.</summary>
    internal static void EnsureCompatible(PlcAddress address, PlcDataType type)
    {
        ArgumentNullException.ThrowIfNull(address);

        var size = PlcValueConverter.SizeOf(type);
        var compatible = type == PlcDataType.Bool
            ? address.IsBit
            : !address.IsBit && address.SizeBytes == size;

        if (!compatible)
        {
            throw new PlcValueException($"Address '{address}' cannot hold a {type} value.");
        }
    }
}
