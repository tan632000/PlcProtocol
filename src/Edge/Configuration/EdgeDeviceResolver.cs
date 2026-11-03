using IndustrialIot.Edge.Commands;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Slmp;
using IndustrialIot.Edge.Polling;

namespace IndustrialIot.Edge.Configuration;

/// <summary>One PLC as the Edge runs it. <see cref="Tags"/> carry the published TagIds.</summary>
public sealed record ResolvedDevice(
    string ChannelName,
    PlcDriverKind Driver,
    string Name,
    string Model,
    string Host,
    int? Port,
    int? Rack,
    int? Slot,
    string? CpuType,
    int TimeoutMs,
    int PollIntervalMs,
    bool Enabled,
    bool WritesEnabled,
    bool LegacyTagIds,
    IReadOnlyList<PlcTagDefinition> Tags);

/// <summary>The effective devices, and whether they came from a single-PLC (v1) configuration.</summary>
public sealed record EdgeDeviceSet(IReadOnlyList<ResolvedDevice> Devices, bool MigratedFromV1);

/// <summary>
/// Turns the composed configuration into the device list: <c>Edge:Channels</c> as written, or the single-PLC keys
/// (<c>Edge:Plc</c>, <c>Edge:Poll</c>, <c>Edge:Tags</c>) migrated in memory to one device that publishes the TagIds an
/// installed Edge already publishes. Nothing is written: the file on disk stays as the operator left it.
/// </summary>
public static class EdgeDeviceResolver
{
    public const string MigratedDeviceName = "Plc1";

    private static readonly string[] V1Sections =
        [PlcProtocolOptions.SectionName, PollOptions.SectionName, $"{EdgeChannelsOptions.SectionName}:Tags"];

    /// <exception cref="InvalidOperationException">Both forms are set, or the configuration is invalid; the message names the key.</exception>
    public static EdgeDeviceSet Resolve(IConfiguration configuration, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var hasChannels = configuration.GetSection(EdgeChannelsOptions.ChannelsKey).Exists();
        var v1Present = V1Sections.Where(section => configuration.GetSection(section).Exists()).ToArray();
        if (hasChannels && v1Present.Length > 0)
        {
            // A schema-2 settings file removes the single-PLC keys, so this is a mix in appsettings or the environment.
            throw new InvalidOperationException(
                $"{string.Join(", ", v1Present)} (one PLC) and {EdgeChannelsOptions.ChannelsKey} (several PLCs) are both "
                + "set; keep one of them.");
        }

        return hasChannels ? FromChannels(configuration) : MigrateV1(configuration, logger);
    }

    private static EdgeDeviceSet FromChannels(IConfiguration configuration)
    {
        var options = configuration.GetSection(EdgeChannelsOptions.SectionName).Get<EdgeChannelsOptions>() ?? new EdgeChannelsOptions();
        var errors = EdgeChannelsValidator.Validate(options);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{EdgeChannelsOptions.ChannelsKey} is invalid. {string.Join(" ", errors)}");
        }

        var devices = new List<ResolvedDevice>();
        foreach (var channel in options.Channels)
        {
            EdgeChannelsValidator.TryParseDriver(channel.Driver, out var driver);
            devices.AddRange(channel.Devices.Select(device => ToDevice(channel, driver, device)));
        }

        return new EdgeDeviceSet(devices, MigratedFromV1: false);
    }

    /// <summary>One configured device as the Edge runs it; the settings API checks proposed devices through it too.</summary>
    internal static ResolvedDevice ToDevice(ChannelOptions channel, PlcDriverKind driver, DeviceOptions device)
    {
        var tags = device.Tags.Select(tag =>
        {
            EdgeChannelsValidator.TryParseDataType(tag.DataType, out var dataType);
            return new PlcTagDefinition
            {
                TagId = EdgeChannelsValidator.PublishedTagId(device, tag),
                Address = tag.Address,
                DataType = dataType,
                Writable = tag.Writable,
                Min = tag.Min,
                Max = tag.Max,
                Length = tag.Length,
                Name = tag.Name,
                Description = tag.Description,
            };
        }).ToArray();

        return new ResolvedDevice(
            channel.Name, driver, device.Name, device.Model, device.Host.Trim(), device.Port, device.Rack, device.Slot,
            CpuType: null, device.TimeoutMs, device.PollIntervalMs, device.Enabled, device.WritesEnabled,
            device.LegacyTagIds, tags);
    }

    /// <summary>
    /// One channel named after the protocol, one device <see cref="MigratedDeviceName"/> with legacy TagIds, every
    /// <c>Edge:Plc</c> key carried, and writes allowed exactly when commands were. The single-PLC validators still
    /// check these keys, so this only maps them.
    /// </summary>
    private static EdgeDeviceSet MigrateV1(IConfiguration configuration, ILogger? logger)
    {
        var rawProtocol = configuration[PlcProtocolOptions.KeyName];
        if (!PlcProtocolOptions.TryParse(rawProtocol, out var protocol))
        {
            throw new InvalidOperationException($"{PlcProtocolOptions.KeyName} must be one of S7, Slmp (was '{rawProtocol}').");
        }

        var plc = configuration.GetSection(PlcProtocolOptions.SectionName);
        var interval = (configuration.GetSection(PollOptions.SectionName).Get<PollOptions>() ?? new PollOptions()).IntervalMs;
        var writes = bool.TryParse(configuration[$"{CommandsOptions.SectionName}:Enabled"], out var enabled) && enabled;
        // A single-PLC tag is named by its TagId and has no description, whatever extra keys the file holds.
        var tags = ((configuration.GetSection(EdgeChannelsOptions.SectionName).Get<PlcTagCatalogOptions>() ?? new PlcTagCatalogOptions()).Tags ?? [])
            .Select(tag => tag with { Name = tag.TagId, Description = null })
            .ToArray();

        ResolvedDevice device;
        if (protocol == PlcProtocol.Slmp)
        {
            var slmp = plc.Get<SlmpOptions>() ?? new SlmpOptions();
            device = new ResolvedDevice(
                nameof(PlcDriverKind.Slmp3E), PlcDriverKind.Slmp3E, MigratedDeviceName, "FX5U", slmp.Host,
                slmp.Port == 0 ? null : slmp.Port, Rack: null, Slot: null, CpuType: null, slmp.TimeoutMs, interval,
                Enabled: true, writes, LegacyTagIds: true, tags);
        }
        else
        {
            var s7 = plc.Get<SiemensS7Options>() ?? new SiemensS7Options();
            if (SiemensS7Options.TryParseCpuType(s7.CpuType, out var cpu) && cpu == S7.Net.CpuType.S71500)
            {
                // The connection keeps the configured CPU type; only the model rules are those of the S7-1200.
                logger?.LogWarning("S7-1500 is not supported; treated as S7-1200 for model rules ({Key} is S71500).", $"{PlcProtocolOptions.SectionName}:CpuType");
            }

            device = new ResolvedDevice(
                nameof(PlcDriverKind.S7), PlcDriverKind.S7, MigratedDeviceName, "S7-1200", s7.Host,
                s7.Port, s7.Rack, s7.Slot, s7.CpuType, s7.TimeoutMs, interval,
                Enabled: true, writes, LegacyTagIds: true, tags);
        }

        return new EdgeDeviceSet([device], MigratedFromV1: true);
    }
}
