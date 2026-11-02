using System.Globalization;
using System.Text.RegularExpressions;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Slmp;

namespace IndustrialIot.Edge.Drivers.Mc1e;

/// <summary>A device and its number on an FX3U. X inputs are read-only; whether an FX3U may be written is the model's rule.</summary>
public sealed record Mc1eAddress(Mc1eDevice Device, int Number) : IPlcAddress
{
    public const string InputReadOnlyText = "X inputs cannot be written";

    public string Canonical => Spell(Device, Number);

    /// <summary>X and Y numbers are octal in GX Works2; D and M are decimal.</summary>
    internal static string Spell(Mc1eDevice device, int number) => device is Mc1eDevice.X or Mc1eDevice.Y
        ? $"{device}{Convert.ToString(number, 8)}"
        : $"{device}{number.ToString(CultureInfo.InvariantCulture)}";

    public bool IsBit => Mc1eFrame.IsBitDevice(Device);

    public string? ReadOnlyReason => Device == Mc1eDevice.X ? InputReadOnlyText : null;

    public override string ToString() => Canonical;
}

/// <summary>
/// FX3U address spelling, case-insensitive: <c>D</c> and <c>M</c> in decimal, <c>X</c> and <c>Y</c> in octal digits.
/// Ranges are those the FX3U-ENET-ADP reaches on an FX3U (User's Manual JY997D45801, section 7.5.1): D0-D7999,
/// M0-M7679, X0-X377, Y0-Y377.
/// </summary>
public sealed partial class Mc1eAddressSyntax : IPlcAddressSyntax
{
    internal const int MaxD = 7999;
    internal const int MaxM = 7679;
    internal const int MaxXY = 0xFF; // 377 octal

    /// <summary>
    /// The longest FX3U String tag: one batch read reaches 64 D registers (section 7.4), two characters each, and a tag
    /// is never split across reads.
    /// </summary>
    public const int MaxStringLength = 2 * Mc1eFrame.MaxReadWords;

    public IPlcAddress Parse(string address, PlcDataType type) => Parse(address, type, null);

    public IPlcAddress Parse(string address, PlcDataType type, int? length)
    {
        var match = address is null ? null : AddressPattern().Match(address.Trim());
        if (match is not { Success: true })
        {
            throw new PlcAddressException(address, "an FX3U address looks like D100, M100, X17 or Y0.");
        }

        var device = char.ToUpperInvariant(match.Groups[1].Value[0]) switch
        {
            'D' => Mc1eDevice.D,
            'M' => Mc1eDevice.M,
            'X' => Mc1eDevice.X,
            _ => Mc1eDevice.Y,
        };

        var digits = match.Groups[2].Value;
        var octal = device is Mc1eDevice.X or Mc1eDevice.Y;
        if (octal && digits.Any(c => c > '7'))
        {
            throw new PlcAddressException(address, $"{device} numbers are octal; '{digits}' has a digit above 7.");
        }

        var number = Convert.ToInt32(digits, octal ? 8 : 10);
        var max = device switch { Mc1eDevice.D => MaxD, Mc1eDevice.M => MaxM, _ => MaxXY };
        if (number > max)
        {
            throw new PlcAddressException(address, $"{Mc1eAddress.Spell(device, number)} is outside {device}0..{Mc1eAddress.Spell(device, max)} on FX3U.");
        }

        var parsed = new Mc1eAddress(device, number);
        if (device != Mc1eDevice.D)
        {
            return type == PlcDataType.Bool
                ? parsed
                : throw new PlcValueException($"Address '{parsed}' cannot hold a {type} value; {device} devices hold Bool.");
        }

        if (type is not (PlcDataType.Int or PlcDataType.Word or PlcDataType.DInt or PlcDataType.DWord or PlcDataType.Real or PlcDataType.String))
        {
            throw new PlcValueException(
                $"Address '{parsed}' cannot hold a {type} value; FX3U D registers hold Int, Word, DInt, DWord, Real or String.");
        }

        if (type == PlcDataType.String && length is not (>= 1 and <= MaxStringLength))
        {
            var given = length?.ToString(CultureInfo.InvariantCulture) ?? "missing";
            throw new PlcValueException($"FX3U String Length must be 1..{MaxStringLength} (was {given}).");
        }

        if (number + SlmpBlockPlanner.WordCount(type, length) - 1 > MaxD)
        {
            throw new PlcValueException($"Address '{parsed}' cannot hold a {type} value: it runs past D{MaxD}.");
        }

        return parsed;
    }

    [GeneratedRegex(@"^([DdMmXxYy])([0-9]{1,5})\z", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();
}
