using System.Text.Json;
using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Drivers;

namespace IndustrialIot.Edge.Settings;

/// <summary>
/// The Channel → Device → Tag tree of the settings API: the running devices as a tree, the page's limits, and the
/// per-device checks of a proposed tree with every problem pinned to its node
/// (<c>Edge:Channels:c:Devices:d[:Tags:t][:Field]</c>).
/// </summary>
internal static class SettingsTree
{
    public const int MaxTagsPerDevice = 1000;
    public const int MaxTagsPerEdge = 5000;

    public const string CpuTypeNotKept = "cpuType-not-kept";

    /// <summary>
    /// The running devices as channels: <c>Edge:Channels</c> as configured, or a single-PLC Edge as its migrated device
    /// (legacy TagIds, each tag named by its TagId), so saving it back publishes the same TagIds.
    /// </summary>
    public static IReadOnlyList<ChannelOptions> Channels(EdgeDeviceSet set)
    {
        var channels = new List<ChannelOptions>();
        foreach (var device in set.Devices)
        {
            if (channels.Count == 0 || channels[^1].Name != device.ChannelName)
            {
                channels.Add(new ChannelOptions { Name = device.ChannelName, Driver = device.Driver.ToString() });
            }

            channels[^1].Devices = [.. channels[^1].Devices, new DeviceOptions
            {
                Name = device.Name,
                Model = device.Model,
                Host = device.Host,
                Port = device.Port,
                Rack = device.Rack,
                Slot = device.Slot,
                TimeoutMs = device.TimeoutMs,
                PollIntervalMs = device.PollIntervalMs,
                Enabled = device.Enabled,
                WritesEnabled = device.WritesEnabled,
                LegacyTagIds = device.LegacyTagIds,
                Tags = [.. device.Tags.Select(tag => new DeviceTagOptions
                {
                    Name = tag.Name ?? tag.TagId,
                    Address = tag.Address,
                    DataType = tag.DataType.ToString(),
                    Length = tag.Length,
                    Writable = tag.Writable,
                    Min = tag.Min,
                    Max = tag.Max,
                    Description = tag.Description,
                })],
            }];
        }

        return channels;
    }

    /// <summary>What a save would lose: a single-PLC S7 whose CpuType is not S71200 (schema 2 has no CpuType).</summary>
    public static IReadOnlyList<string> Warnings(EdgeDeviceSet set) =>
        set.MigratedFromV1 && set.Devices.Any(device => device.Driver == PlcDriverKind.S7
            && SiemensS7Options.TryParseCpuType(device.CpuType, out var cpu) && cpu != S7.Net.CpuType.S71200)
            ? [CpuTypeNotKept]
            : [];

    /// <summary>The models the page offers, with what choosing one fills in.</summary>
    public static object Models() => PlcModelCatalog.Models.Select(model => new
    {
        model.Name,
        Driver = model.Driver.ToString(),
        model.DefaultPort,
        model.DefaultRack,
        model.DefaultSlot,
        PortHint = model.PortHint.Length == 0 ? null : model.PortHint,
        model.DataTypes,
        WritesAllowed = model.WritesVerified,
    }).ToArray();

    /// <summary>
    /// Checks what binding cannot see: every channel, device and tag is a non-empty object (an empty one would shift
    /// the binder's indices against the page), no object repeats a key in another case (the binder would merge both,
    /// past the counts below), and the tag limits, counted on the JSON.
    /// </summary>
    public static void CheckShapeAndLimits(JsonElement settings, List<SettingsError> errors)
    {
        if (HasRepeatedKey(settings, $"{EdgeChannelsOptions.ChannelsKey}:0", errors))
        {
            return;
        }

        var total = 0;
        var c = 0;
        foreach (var channel in Items(settings, "Channels"))
        {
            var channelKey = $"{EdgeChannelsOptions.ChannelsKey}:{c++}";
            if (IsEmptyObject(channel, channelKey, errors) || HasRepeatedKey(channel, channelKey, errors))
            {
                continue;
            }

            var d = 0;
            foreach (var device in Items(channel, "Devices"))
            {
                var deviceKey = $"{channelKey}:Devices:{d++}";
                if (IsEmptyObject(device, deviceKey, errors) || HasRepeatedKey(device, deviceKey, errors))
                {
                    continue;
                }

                var t = 0;
                foreach (var tag in Items(device, "Tags"))
                {
                    var tagKey = $"{deviceKey}:Tags:{t++}";
                    _ = IsEmptyObject(tag, tagKey, errors) || HasRepeatedKey(tag, tagKey, errors);
                }

                if (t > MaxTagsPerDevice)
                {
                    errors.Add(new SettingsError($"{deviceKey}:Tags", $"At most {MaxTagsPerDevice} tags per PLC."));
                }

                if (total <= MaxTagsPerEdge && (total += t) > MaxTagsPerEdge)
                {
                    errors.Add(new SettingsError($"{deviceKey}:Tags", $"At most {MaxTagsPerEdge} tags per Edge."));
                }
            }
        }
    }

    /// <summary>
    /// The registry's own checks of every proposed device (driver options, poll range, tag catalog), pinned to the
    /// device or tag. Devices of a channel whose driver is unknown are left to the validator's Driver error.
    /// </summary>
    public static IEnumerable<SettingsError> DeviceErrors(EdgeChannelsOptions options)
    {
        for (var c = 0; c < options.Channels.Length; c++)
        {
            var channel = options.Channels[c];
            if (!EdgeChannelsValidator.TryParseDriver(channel.Driver, out var driver))
            {
                continue;
            }

            for (var d = 0; d < channel.Devices.Length; d++)
            {
                var device = channel.Devices[d];
                var deviceKey = $"{EdgeChannelsOptions.ChannelsKey}:{c}:Devices:{d}";
                foreach (var problem in PlcDeviceRegistry.Check(EdgeDeviceResolver.ToDevice(channel, driver, device)))
                {
                    var path = $"{deviceKey}:{problem.Field}";
                    if (problem.TagId is { } tagId)
                    {
                        var t = Array.FindIndex(device.Tags, tag =>
                            tag.Name is not null && EdgeChannelsValidator.PublishedTagId(device, tag).Equals(tagId, StringComparison.OrdinalIgnoreCase));
                        path = t >= 0 ? $"{deviceKey}:Tags:{t}:{problem.Field}" : $"{deviceKey}:Tags";
                    }

                    yield return new SettingsError(path, SettingsService.FixedMessage(path));
                }
            }
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in parent.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    yield return item;
                }

                yield break;
            }
        }
    }

    private static bool HasRepeatedKey(JsonElement item, string key, List<SettingsError> errors)
    {
        if (item.ValueKind == JsonValueKind.Object
            && item.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            errors.Add(new SettingsError(key, "A key appears twice (keys are case-insensitive)."));
            return true;
        }

        return false;
    }

    private static bool IsEmptyObject(JsonElement item, string key, List<SettingsError> errors)
    {
        if (item.ValueKind == JsonValueKind.Object && !item.EnumerateObject().Any())
        {
            errors.Add(new SettingsError(key, "Must not be empty."));
            return true;
        }

        return false;
    }
}
