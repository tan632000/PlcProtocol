using System.Text.RegularExpressions;
using IndustrialIot.Edge.Drivers;

namespace IndustrialIot.Edge.Configuration;

/// <summary>The protocol a channel speaks; each device on it uses one driver instance of that protocol.</summary>
public enum PlcDriverKind
{
    S7,
    Slmp3E,
    Mc1E,
}

/// <summary>
/// The multi-device configuration <c>Edge:Channels</c>: channel → device → tag. Bound from <c>Edge</c> so the
/// array <c>Edge:Channels</c> maps to <see cref="Channels"/>; <see cref="EdgeChannelsValidator"/> checks it.
/// </summary>
public sealed class EdgeChannelsOptions
{
    public const string SectionName = "Edge";

    public const string ChannelsKey = "Edge:Channels";

    public ChannelOptions[] Channels { get; set; } = [];
}

public sealed class ChannelOptions
{
    public string Name { get; set; } = "";

    /// <summary><c>S7</c>, <c>Slmp3E</c> or <c>Mc1E</c>.</summary>
    public string Driver { get; set; } = "";

    public DeviceOptions[] Devices { get; set; } = [];
}

public sealed class DeviceOptions
{
    public string Name { get; set; } = "";

    /// <summary><c>S7-1200</c>, <c>FX5U</c> or <c>FX3U</c>; must belong to the channel's driver.</summary>
    public string Model { get; set; } = "";

    public string Host { get; set; } = "";

    /// <summary>Port, rack and slot left out take the model's defaults.</summary>
    public int? Port { get; set; }

    public int? Rack { get; set; }

    public int? Slot { get; set; }

    public int TimeoutMs { get; set; } = 3000;

    public int PollIntervalMs { get; set; } = 1000;

    public bool Enabled { get; set; } = true;

    /// <summary>Off by default: a write also needs <c>Edge:Commands:Enabled</c> and the tag's <c>Writable</c>.</summary>
    public bool WritesEnabled { get; set; }

    /// <summary>When true every tag publishes its <c>Name</c> verbatim (devices migrated from a single-PLC Edge).</summary>
    public bool LegacyTagIds { get; set; }

    public DeviceTagOptions[] Tags { get; set; } = [];
}

public sealed class DeviceTagOptions
{
    public string Name { get; set; } = "";

    public string Address { get; set; } = "";

    /// <summary>A <see cref="PlcDataType"/> name; kept as text so a wrong one is reported with its key.</summary>
    public string DataType { get; set; } = "";

    /// <summary>Characters of a <c>String</c> tag.</summary>
    public int? Length { get; set; }

    public bool Writable { get; set; }

    public decimal? Min { get; set; }

    public decimal? Max { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Checks names, models, uniqueness and published TagIds of <see cref="EdgeChannelsOptions"/>, and each device against
/// its model (<see cref="PlcModelCatalog"/>): driver, port, data types. Every error starts with the full configuration
/// key. Addresses against a device's driver are checked by its tag catalog.
/// </summary>
public static partial class EdgeChannelsValidator
{
    public const int MaxTagIdLength = 64;

    public static IReadOnlyList<string> Validate(EdgeChannelsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        var channelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Same comparer as the tag catalog's lookup, so two ids that differ only in case cannot both exist.
        var tagIds = new Dictionary<string, (string Id, string Key)>(StringComparer.OrdinalIgnoreCase);

        for (var c = 0; c < options.Channels.Length; c++)
        {
            var channel = options.Channels[c];
            var channelKey = $"{EdgeChannelsOptions.ChannelsKey}:{c}";
            CheckName(errors, $"{channelKey}:Name", channel.Name, channelNames);

            var hasDriver = TryParseDriver(channel.Driver, out var driver);
            if (!hasDriver)
            {
                errors.Add($"{channelKey}:Driver must be one of S7, Slmp3E, Mc1E (was '{channel.Driver}').");
            }

            for (var d = 0; d < channel.Devices.Length; d++)
            {
                var device = channel.Devices[d];
                var deviceKey = $"{channelKey}:Devices:{d}";
                CheckName(errors, $"{deviceKey}:Name", device.Name, deviceNames);

                if (!PlcModelCatalog.TryGet(device.Model, out var model))
                {
                    errors.Add($"{deviceKey}:Model must be one of {string.Join(", ", PlcModelCatalog.Models.Select(m => m.Name))} (was '{device.Model}').");
                }
                else if (hasDriver && model.Driver != driver)
                {
                    errors.Add($"{deviceKey}:Model {model.Name} needs a {model.Driver} channel, not {driver}.");
                }

                if (model is not null && PlcModelCatalog.CheckPort(model, device.Port) is { } portProblem)
                {
                    errors.Add($"{deviceKey}:Port {portProblem}");
                }

                if (model is not null && PlcModelCatalog.CheckWrites(model) is { } writeGuard)
                {
                    if (device.WritesEnabled)
                    {
                        errors.Add($"{deviceKey}:WritesEnabled {writeGuard}");
                    }

                    for (var t = 0; t < device.Tags.Length; t++)
                    {
                        if (device.Tags[t].Writable)
                        {
                            errors.Add($"{deviceKey}:Tags:{t}:Writable {writeGuard}");
                        }
                    }
                }

                var defaultPort = model?.DefaultPort;
                if (string.IsNullOrWhiteSpace(device.Host))
                {
                    errors.Add($"{deviceKey}:Host is required.");
                }
                else if ((device.Port ?? defaultPort) is { } port && !endpoints.Add($"{device.Host.Trim()}:{port}"))
                {
                    errors.Add($"{deviceKey}:Host {device.Host}:{port} is already used by another device.");
                }

                CheckTags(errors, deviceKey, device, model, tagIds);
            }
        }

        return errors;
    }

    /// <summary>The TagId telemetry and commands use: the name alone on a legacy device, else device.name.</summary>
    public static string PublishedTagId(DeviceOptions device, DeviceTagOptions tag) =>
        device.LegacyTagIds ? tag.Name : $"{device.Name}.{tag.Name}";

    /// <summary>Case-insensitive, names only (a numeric string is not a driver).</summary>
    public static bool TryParseDriver(string? value, out PlcDriverKind driver) =>
        TryParseName(value, out driver);

    internal static bool TryParseDataType(string? value, out PlcDataType dataType) =>
        TryParseName(value, out dataType);

    private static void CheckTags(
        List<string> errors, string deviceKey, DeviceOptions device, PlcModel? model, Dictionary<string, (string Id, string Key)> tagIds)
    {
        for (var t = 0; t < device.Tags.Length; t++)
        {
            var tag = device.Tags[t];
            var tagKey = $"{deviceKey}:Tags:{t}";
            if (tag.Name is null || !TagNamePattern().IsMatch(tag.Name))
            {
                errors.Add($"{tagKey}:Name must be [A-Za-z0-9_.-] and not empty (was '{tag.Name}').");
            }
            else
            {
                var tagId = PublishedTagId(device, tag);
                if (tagId.Length > MaxTagIdLength)
                {
                    errors.Add($"{tagKey}:Name gives TagId '{tagId}' of {tagId.Length} chars; at most {MaxTagIdLength}.");
                }
                else if (!tagIds.TryAdd(tagId, (tagId, tagKey)))
                {
                    var (usedId, usedKey) = tagIds[tagId];
                    errors.Add($"{tagKey}:Name gives TagId '{tagId}', the same as '{usedId}' of {usedKey} (ids are case-insensitive).");
                }
            }

            if (string.IsNullOrWhiteSpace(tag.Address))
            {
                errors.Add($"{tagKey}:Address is required.");
            }

            if (!TryParseDataType(tag.DataType, out _))
            {
                errors.Add($"{tagKey}:DataType must be one of {string.Join(", ", Enum.GetNames<PlcDataType>())} (was '{tag.DataType}').");
            }
            else if (model is not null && PlcModelCatalog.CheckTag(model, tag.Address, tag.DataType) is var (field, message))
            {
                errors.Add($"{tagKey}:{field} {message}");
            }
        }
    }

    private static void CheckName(List<string> errors, string key, string? name, HashSet<string> seen)
    {
        if (name is null || !NamePattern().IsMatch(name))
        {
            errors.Add($"{key} must be 1..32 chars [A-Za-z0-9_-] (was '{name}').");
        }
        else if (!seen.Add(name))
        {
            errors.Add($"{key} '{name}' is already used (names are case-insensitive).");
        }
    }

    private static bool TryParseName<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        var name = Enum.GetNames<TEnum>().FirstOrDefault(n => n.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out result);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagNamePattern();
}
