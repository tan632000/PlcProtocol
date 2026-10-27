using System.Globalization;
using System.Text.RegularExpressions;
using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Drivers.Slmp;

/// <summary>SLMP device codes (binary 3E frame). The closed set of devices the driver can name in a read.</summary>
public enum SlmpDevice : byte
{
    D = 0xA8,
    M = 0x90,
    X = 0x9C,
    Y = 0x9D,
}

/// <summary>A device and its number. Every SLMP address is read-only: the driver cannot write.</summary>
public sealed record SlmpAddress(SlmpDevice Device, int Number) : IPlcAddress
{
    public const string ReadOnlyText = "SLMP is read-only";

    public string Canonical => Spell(Device, Number);

    /// <summary>X and Y numbers are octal in GX Works3; D and M are decimal.</summary>
    internal static string Spell(SlmpDevice device, int number) => device is SlmpDevice.X or SlmpDevice.Y
        ? $"{device}{Convert.ToString(number, 8)}"
        : $"{device}{number.ToString(CultureInfo.InvariantCulture)}";

    public bool IsBit => Device != SlmpDevice.D;

    public string? ReadOnlyReason => ReadOnlyText;

    public override string ToString() => Canonical;
}

/// <summary>
/// FX5U address spelling, case-insensitive: <c>D&lt;n&gt;</c> and <c>M&lt;n&gt;</c> in decimal, <c>X&lt;n&gt;</c> and
/// <c>Y&lt;n&gt;</c> in octal digits. The stored number is the device number (X17 is 15).
/// </summary>
public sealed partial class SlmpAddressSyntax : IPlcAddressSyntax
{
    // [UNVERIFIED] FX5U default ranges; settle against GX Works3 device settings (task-07).
    internal const int MaxD = 7999;
    internal const int MaxM = 7679;
    internal const int MaxXY = 0x3FF; // 1777 octal

    public IPlcAddress Parse(string address, PlcDataType type)
    {
        var match = address is null ? null : AddressPattern().Match(address.Trim());
        if (match is not { Success: true })
        {
            throw new PlcAddressException(address, "an SLMP address looks like D100, M100, X17 or Y0.");
        }

        var device = char.ToUpperInvariant(match.Groups[1].Value[0]) switch
        {
            'D' => SlmpDevice.D,
            'M' => SlmpDevice.M,
            'X' => SlmpDevice.X,
            _ => SlmpDevice.Y,
        };
        var digits = match.Groups[2].Value;
        var octal = device is SlmpDevice.X or SlmpDevice.Y;
        if (octal && digits.Any(c => c > '7'))
        {
            throw new PlcAddressException(address, $"{device} numbers are octal; '{digits}' has a digit above 7.");
        }

        var number = Convert.ToInt32(digits, octal ? 8 : 10);
        var max = device switch { SlmpDevice.D => MaxD, SlmpDevice.M => MaxM, _ => MaxXY };
        if (number > max)
        {
            throw new PlcAddressException(address, $"{SlmpAddress.Spell(device, number)} is outside {device}0..{SlmpAddress.Spell(device, max)}.");
        }

        var parsed = new SlmpAddress(device, number);
        if (device != SlmpDevice.D)
        {
            return type == PlcDataType.Bool
                ? parsed
                : throw new PlcValueException($"Address '{parsed}' cannot hold a {type} value; {device} devices hold Bool.");
        }

        if (type is not (PlcDataType.Int or PlcDataType.Word or PlcDataType.DInt or PlcDataType.DWord or PlcDataType.Real))
        {
            throw new PlcValueException($"Address '{parsed}' cannot hold a {type} value; D registers hold Int, Word, DInt, DWord or Real.");
        }

        if (number + SlmpBlockPlanner.WordCount(type) - 1 > MaxD)
        {
            throw new PlcValueException($"Address '{parsed}' cannot hold a {type} value: it runs past D{MaxD}.");
        }

        return parsed;
    }

    [GeneratedRegex(@"^([DdMmXxYy])([0-9]{1,5})\z", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();
}
