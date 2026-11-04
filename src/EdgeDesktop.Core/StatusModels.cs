using System.Text.Json;

namespace IndustrialIot.EdgeDesktop.Core;

// Read-only copies of the Edge's GET /api/status JSON (src/Edge/Monitoring/MonitorServer.cs, MonitorStatus and the
// records after it). Positional, so a strict read in the contract tests fails on any missing or renamed property.

/// <summary>The whole status. <see cref="Plc"/> and <see cref="PollIntervalMs"/> are null on a multi-PLC Edge.</summary>
public sealed record EdgeStatus(
    string EdgeId,
    DateTimeOffset NowUtc,
    int? PollIntervalMs,
    DateTimeOffset? LastCycleUtc,
    double? ReadMs,
    PlcInfo? Plc,
    IReadOnlyList<DeviceInfo> Devices,
    MqttInfo Mqtt,
    long? BufferPending,
    IReadOnlyList<TagInfo> Tags,
    SettingsApplyInfo SettingsApply);

/// <summary>The only PLC of a single-PLC Edge. <see cref="Rack"/>/<see cref="Slot"/> are null for Slmp.</summary>
public sealed record PlcInfo(string Protocol, string Host, int Port, int? Rack, int? Slot, int TimeoutMs, string State);

public sealed record DeviceInfo(
    string Name,
    string Model,
    string Host,
    int Port,
    string State,
    DateTimeOffset? LastGoodUtc,
    string? LastError,
    double? ReadMs,
    int? Requests);

public sealed record MqttInfo(string Host, int Port, bool Connected);

public sealed record TagInfo(
    string TagId,
    string Device,
    string Address,
    string DataType,
    bool Writable,
    JsonElement? Value,
    string? Quality,
    DateTimeOffset? TimestampUtc,
    string? Error);

public sealed record SettingsApplyInfo(string Revision, string? ApplyId, string State, string? Error);
