using System.Globalization;
using System.Text.Json.Nodes;

namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>Where an API error belongs: a channel, optionally a device and a tag of it, and the field, if named.</summary>
public sealed record ErrorLocation(DraftChannel Channel, DraftDevice? Device, DraftTag? Tag, string? Field);

/// <summary>
/// The operator's editable copy of the Edge's settings, from one <c>GET /api/settings</c>. Nodes are edited by
/// reference, never by position, so an edit always lands on the node it was made for; one that is no longer in the draft
/// is refused. It remembers the revision it came from (the save sends it back) and whether it has unsaved edits or can no
/// longer be saved (stale).
/// </summary>
public sealed class ConfigDraft
{
    private const string ChannelsPrefix = "Edge:Channels:";

    private readonly List<DraftChannel> _channels;

    public ConfigDraft(SettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Revision = snapshot.Revision;
        Models = snapshot.Models;
        Warnings = snapshot.Warnings;
        CommandsEnabled = snapshot.CommandsEnabled;
        PasswordSet = snapshot.PasswordSet;
        _channels = [.. snapshot.Settings.Channels.Select(DraftChannel.From)];
    }

    public string Revision { get; }

    public IReadOnlyList<PlcModelInfo> Models { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool CommandsEnabled { get; }

    public bool PasswordSet { get; }

    public IReadOnlyList<DraftChannel> Channels => _channels;

    /// <summary>Edited since it was loaded.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>The Edge's settings changed under it: kept for reading, never saved.</summary>
    public bool IsStale { get; private set; }

    /// <summary>A channel without PLC would be saved and then vanish, since the Edge lists channels by their devices.</summary>
    public IReadOnlyList<DraftChannel> ChannelsWithoutDevices => [.. _channels.Where(c => c.DeviceList.Count == 0)];

    public void MarkStale() => IsStale = true;

    public DraftChannel AddChannel(DraftChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        RequireNew(_channels.Contains(channel));
        _channels.Add(channel);
        IsDirty = true;
        return channel;
    }

    public void RemoveChannel(DraftChannel channel)
    {
        Require(_channels.Remove(channel));
        IsDirty = true;
    }

    public DraftDevice AddDevice(DraftChannel channel, DraftDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Require(_channels.Contains(channel));
        RequireNew(_channels.Any(c => c.DeviceList.Contains(device)));
        channel.DeviceList.Add(device);
        IsDirty = true;
        return device;
    }

    public void RemoveDevice(DraftDevice device)
    {
        ChannelOf(device).DeviceList.Remove(device);
        IsDirty = true;
    }

    public DraftTag AddTag(DraftDevice device, DraftTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        _ = ChannelOf(device);
        RequireNew(_channels.SelectMany(c => c.DeviceList).Any(d => d.TagList.Contains(tag)));
        device.TagList.Add(tag);
        IsDirty = true;
        return tag;
    }

    public void RemoveTag(DraftTag tag)
    {
        DeviceOf(tag).TagList.Remove(tag);
        IsDirty = true;
    }

    /// <summary>Name and driver of <paramref name="target"/> from <paramref name="edited"/>; its devices stay.</summary>
    public void Update(DraftChannel target, DraftChannel edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        Require(_channels.Contains(target));
        target.Name = edited.Name;
        target.Driver = edited.Driver;
        IsDirty = true;
    }

    /// <summary>Every field of <paramref name="target"/> from <paramref name="edited"/> but its tags.</summary>
    public void Update(DraftDevice target, DraftDevice edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        _ = ChannelOf(target);
        target.CopyFrom(edited);
        IsDirty = true;
    }

    public void Update(DraftTag target, DraftTag edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        _ = DeviceOf(target);
        target.CopyFrom(edited);
        IsDirty = true;
    }

    public DraftChannel ChannelOf(DraftDevice device) =>
        _channels.FirstOrDefault(c => c.DeviceList.Contains(device)) ?? throw Detached();

    public DraftDevice DeviceOf(DraftTag tag) =>
        _channels.SelectMany(c => c.DeviceList).FirstOrDefault(d => d.TagList.Contains(tag)) ?? throw Detached();

    /// <summary>The <c>settings</c> object of a validate or save body, with the keys the web page sends.</summary>
    public JsonObject ToSettingsJson() => new()
    {
        ["Channels"] = new JsonArray([.. _channels.Select(channel => (JsonNode)new JsonObject
        {
            ["Name"] = channel.Name,
            ["Driver"] = channel.Driver,
            ["Devices"] = new JsonArray([.. channel.DeviceList.Select(device => (JsonNode)new JsonObject
            {
                ["Name"] = device.Name,
                ["Model"] = device.Model,
                ["Host"] = device.Host,
                ["Port"] = device.Port,
                ["Rack"] = device.Rack,
                ["Slot"] = device.Slot,
                ["TimeoutMs"] = device.TimeoutMs,
                ["PollIntervalMs"] = device.PollIntervalMs,
                ["Enabled"] = device.Enabled,
                ["WritesEnabled"] = device.WritesEnabled,
                ["LegacyTagIds"] = device.LegacyTagIds,
                ["Tags"] = new JsonArray([.. device.TagList.Select(tag => (JsonNode)new JsonObject
                {
                    ["Name"] = tag.Name,
                    ["Address"] = tag.Address,
                    ["DataType"] = tag.DataType,
                    ["Length"] = tag.Length,
                    ["Writable"] = tag.Writable,
                    ["Min"] = tag.Min,
                    ["Max"] = tag.Max,
                    ["Description"] = tag.Description,
                })]),
            })]),
        })]),
    };

    /// <summary>
    /// The node and field of an error path <c>Edge:Channels:c[:Devices:d[:Tags[:t]]][:Field]</c> in this draft; null for
    /// any other path or an index the draft does not have (the caller shows it verbatim).
    /// </summary>
    public ErrorLocation? Locate(string path)
    {
        if (path is null || !path.StartsWith(ChannelsPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = path[ChannelsPrefix.Length..].Split(':');
        if (!TryIndex(parts[0], _channels.Count, out var c))
        {
            return null;
        }

        var channel = _channels[c];
        if (parts.Length == 1)
        {
            return new ErrorLocation(channel, null, null, null);
        }

        if (parts[1] != "Devices" || parts.Length == 2)
        {
            return parts.Length == 2 ? new ErrorLocation(channel, null, null, parts[1]) : null;
        }

        if (!TryIndex(parts[2], channel.DeviceList.Count, out var d))
        {
            return null;
        }

        var device = channel.DeviceList[d];
        if (parts.Length == 3)
        {
            return new ErrorLocation(channel, device, null, null);
        }

        if (parts[3] != "Tags" || parts.Length == 4)
        {
            return parts.Length == 4 ? new ErrorLocation(channel, device, null, parts[3]) : null;
        }

        if (!TryIndex(parts[4], device.TagList.Count, out var t))
        {
            return null;
        }

        var tag = device.TagList[t];
        return parts.Length switch
        {
            5 => new ErrorLocation(channel, device, tag, null),
            6 => new ErrorLocation(channel, device, tag, parts[5]),
            _ => null,
        };
    }

    private static bool TryIndex(string text, int count, out int index) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index < count;

    private static void Require(bool inDraft)
    {
        if (!inDraft)
        {
            throw Detached();
        }
    }

    // The same node twice would make one edit change both places.
    private static void RequireNew(bool alreadyInDraft)
    {
        if (alreadyInDraft)
        {
            throw new InvalidOperationException("The node is already part of this draft.");
        }
    }

    private static InvalidOperationException Detached() => new("The node is no longer part of this draft.");
}
