using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using IndustrialIot.Edge.Configuration;

namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// One supported PLC model: the driver it needs, the connection defaults an omitted key takes, and the data types its
/// tags may use. <see cref="PortHint"/> says where the port is set when the model has no default.
/// </summary>
public sealed record PlcModel(
    string Name,
    PlcDriverKind Driver,
    int? DefaultPort,
    int? DefaultRack,
    int? DefaultSlot,
    IReadOnlyList<string> DataTypes,
    string PortHint,
    bool WritesVerified = true)
{
    public bool Supports(string? dataType) => DataTypes.Contains(dataType?.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The models a device may name (plan D-04). The user picks a model; its driver, defaults and limits follow. Type lists
/// already include the types a later version adds, so an unsupported pair is refused as soon as the type exists.
/// </summary>
public static partial class PlcModelCatalog
{
    private const int S7Port = 102;

    // FX3U device ranges as the Ethernet adapter reaches them (FX3U-ENET-ADP User's Manual JY997D45801, section 7.5.1).
    private const int Fx3uMaxD = 7999;
    private const int Fx3uMaxM = 7679;
    private const int Fx3uMaxXY = 0xFF; // 377 octal

    public static IReadOnlyList<PlcModel> Models { get; } =
    [
        new("S7-1200", PlcDriverKind.S7, S7Port, 0, 1,
            ["Bool", "Byte", "Word", "Int", "DWord", "DInt", "Real", "String", "LReal", "DTL"], ""),
        new("FX5U", PlcDriverKind.Slmp3E, null, null, null,
            ["Bool", "Word", "Int", "DWord", "DInt", "Real", "String", "LReal"], "use the SLMP port set in GX Works3"),
        new("FX3U", PlcDriverKind.Mc1E, null, null, null,
            ["Bool", "Word", "Int", "DWord", "DInt", "Real", "String"], "use the MC protocol port set in GX Works2 for the Ethernet module",
            WritesVerified: false),
    ];

    public static bool TryGet(string? name, [NotNullWhen(true)] out PlcModel? model)
    {
        model = Models.FirstOrDefault(m => m.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));
        return model is not null;
    }

    /// <summary>
    /// The write guard (plan D-05): a model whose writes are not yet verified on a real PLC (FX3U) refuses
    /// <c>WritesEnabled</c> and <c>Writable</c> at start. Null when writes are allowed. The text has no key prefix.
    /// </summary>
    public static string? CheckWrites(PlcModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.WritesVerified ? null : $"{model.Name} writes are disabled until verified on a real {model.Name}.";
    }

    /// <summary>The model's port rule for <paramref name="port"/>; null when it is fine. The text has no key prefix.</summary>
    public static string? CheckPort(PlcModel model, int? port)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.DefaultPort is not null)
        {
            return null;
        }

        return port switch
        {
            null => $"is required for {model.Name}; {model.PortHint}.",
            S7Port => $"{S7Port} is the S7 port; {model.PortHint}.",
            _ => null,
        };
    }

    /// <summary>
    /// The model's rules for one tag: the data type, and for the FX3U the device ranges. Returns the failing field
    /// (<c>DataType</c> or <c>Address</c>) and its message, or null. Other models' addresses are checked by their
    /// driver's address syntax.
    /// </summary>
    public static (string Field, string Message)? CheckTag(PlcModel model, string? address, string? dataType)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Supports(dataType))
        {
            return ("DataType", $"{dataType} is not supported by {model.Name} (it supports {string.Join(", ", model.DataTypes)}).");
        }

        return model.Driver == PlcDriverKind.Mc1E && Fx3uAddress(address, dataType!) is { } problem ? ("Address", problem) : null;
    }

    /// <summary>D, M, X, Y in the FX3U ranges; X/Y octal; bit devices hold Bool, D registers the word types.</summary>
    private static string? Fx3uAddress(string? address, string dataType)
    {
        var match = address is null ? null : Fx3uPattern().Match(address.Trim());
        if (match is not { Success: true })
        {
            return $"'{address}' is not an FX3U address; it looks like D100, M100, X17 or Y0.";
        }

        var device = char.ToUpperInvariant(match.Groups[1].Value[0]);
        var digits = match.Groups[2].Value;
        var octal = device is 'X' or 'Y';
        if (octal && digits.Any(c => c > '7'))
        {
            return $"{device} numbers are octal; '{digits}' has a digit above 7.";
        }

        var number = Convert.ToInt32(digits, octal ? 8 : 10);
        var max = device switch { 'D' => Fx3uMaxD, 'M' => Fx3uMaxM, _ => Fx3uMaxXY };
        if (number > max)
        {
            var last = octal ? Convert.ToString(max, 8) : max.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"{device}{digits} is outside {device}0..{device}{last} on FX3U.";
        }

        var isBool = dataType.Trim().Equals("Bool", StringComparison.OrdinalIgnoreCase);
        if (device != 'D')
        {
            return isBool ? null : $"{device} devices hold Bool, not {dataType}.";
        }

        if (isBool)
        {
            return "D registers hold word types, not Bool.";
        }

        var words = dataType.Trim().ToUpperInvariant() switch { "DINT" or "DWORD" or "REAL" => 2, _ => 1 };
        return number + words - 1 > Fx3uMaxD ? $"a {dataType} at D{number} runs past D{Fx3uMaxD}." : null;
    }

    [GeneratedRegex(@"^([DdMmXxYy])([0-9]{1,5})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Fx3uPattern();
}
