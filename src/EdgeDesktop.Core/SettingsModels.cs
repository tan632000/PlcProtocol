namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>
/// The reply of <c>GET /api/settings</c> (src/Edge/Settings/SettingsService.cs GetAsync). Positional, so the contract
/// tests' strict read fails on any property the Edge renames or removes.
/// </summary>
public sealed record SettingsSnapshot(
    string Revision,
    string Source,
    ChannelTree Settings,
    IReadOnlyList<PlcModelInfo> Models,
    IReadOnlyList<string> Warnings,
    bool CommandsEnabled,
    bool PasswordSet);

public sealed record ChannelTree(IReadOnlyList<ChannelSettings> Channels);

public sealed record ChannelSettings(string Name, string Driver, IReadOnlyList<DeviceSettings> Devices);

public sealed record DeviceSettings(
    string Name,
    string Model,
    string Host,
    int? Port,
    int? Rack,
    int? Slot,
    int TimeoutMs,
    int PollIntervalMs,
    bool Enabled,
    bool WritesEnabled,
    bool LegacyTagIds,
    IReadOnlyList<TagSettings> Tags);

public sealed record TagSettings(
    string Name,
    string Address,
    string DataType,
    int? Length,
    bool Writable,
    decimal? Min,
    decimal? Max,
    string? Description);

/// <summary>A PLC model of the Edge's catalog: what choosing it fills in, its data types, and whether it may write.</summary>
public sealed record PlcModelInfo(
    string Name,
    string Driver,
    int? DefaultPort,
    int? DefaultRack,
    int? DefaultSlot,
    string? PortHint,
    IReadOnlyList<string> DataTypes,
    bool WritesAllowed);

/// <summary>An error of the settings API, pinned to a configuration path such as <c>Edge:Channels:0:Name</c>.</summary>
public sealed record ApiError(string Path, string Message);

/// <summary>A channel of the draft: edited in place, sent with the web page's keys.</summary>
public sealed class DraftChannel
{
    internal List<DraftDevice> DeviceList { get; set; } = [];

    public string Name { get; set; } = "";

    /// <summary><c>S7</c>, <c>Slmp3E</c> or <c>Mc1E</c>.</summary>
    public string Driver { get; set; } = "";

    public IReadOnlyList<DraftDevice> Devices => DeviceList;

    public DraftChannel Clone() => new() { Name = Name, Driver = Driver, DeviceList = [.. DeviceList.Select(d => d.Clone())] };

    internal static DraftChannel From(ChannelSettings channel) =>
        new() { Name = channel.Name, Driver = channel.Driver, DeviceList = [.. channel.Devices.Select(DraftDevice.From)] };
}

public sealed class DraftDevice
{
    internal List<DraftTag> TagList { get; set; } = [];

    public string Name { get; set; } = "";

    public string Model { get; set; } = "";

    public string Host { get; set; } = "";

    public int? Port { get; set; }

    public int? Rack { get; set; }

    public int? Slot { get; set; }

    public int TimeoutMs { get; set; } = 3000;

    public int PollIntervalMs { get; set; } = 1000;

    public bool Enabled { get; set; } = true;

    public bool WritesEnabled { get; set; }

    public bool LegacyTagIds { get; set; }

    public IReadOnlyList<DraftTag> Tags => TagList;

    public DraftDevice Clone()
    {
        var copy = new DraftDevice { TagList = [.. TagList.Select(t => t.Clone())] };
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Every field but the tags, which are edited one by one.</summary>
    internal void CopyFrom(DraftDevice other)
    {
        Name = other.Name;
        Model = other.Model;
        Host = other.Host;
        Port = other.Port;
        Rack = other.Rack;
        Slot = other.Slot;
        TimeoutMs = other.TimeoutMs;
        PollIntervalMs = other.PollIntervalMs;
        Enabled = other.Enabled;
        WritesEnabled = other.WritesEnabled;
        LegacyTagIds = other.LegacyTagIds;
    }

    internal static DraftDevice From(DeviceSettings device) => new()
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
        TagList = [.. device.Tags.Select(DraftTag.From)],
    };
}

public sealed class DraftTag
{
    public string Name { get; set; } = "";

    public string Address { get; set; } = "";

    public string DataType { get; set; } = "";

    /// <summary>Characters of a <c>String</c> tag.</summary>
    public int? Length { get; set; }

    public bool Writable { get; set; }

    public decimal? Min { get; set; }

    public decimal? Max { get; set; }

    public string? Description { get; set; }

    public DraftTag Clone() => (DraftTag)MemberwiseClone();

    internal void CopyFrom(DraftTag other)
    {
        Name = other.Name;
        Address = other.Address;
        DataType = other.DataType;
        Length = other.Length;
        Writable = other.Writable;
        Min = other.Min;
        Max = other.Max;
        Description = other.Description;
    }

    internal static DraftTag From(TagSettings tag) => new()
    {
        Name = tag.Name,
        Address = tag.Address,
        DataType = tag.DataType,
        Length = tag.Length,
        Writable = tag.Writable,
        Min = tag.Min,
        Max = tag.Max,
        Description = tag.Description,
    };
}
