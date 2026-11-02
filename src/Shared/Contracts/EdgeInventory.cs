using System.Text.RegularExpressions;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Shared.Contracts;

/// <summary>
/// One part of an Edge's inventory round: its PLCs (status entries) and/or the tags of one PLC. An MQTT packet is at
/// most 16 KiB, so the Edge splits a round into parts; the Cloud stores the round once all <see cref="PartCount"/> parts
/// of (<see cref="EdgeId"/>, <see cref="BootId"/>, <see cref="Round"/>) arrived. Carries no host, port, address or
/// error text: Viewers read it.
/// </summary>
public sealed record EdgeInventory : IContract
{
    public const int MaxParts = 200;

    public required int SchemaVersion { get; init; }

    public required string EdgeId { get; init; }

    /// <summary>New for every start of the Edge agent (a settings apply included); <see cref="Round"/> restarts at 0.</summary>
    public required Guid BootId { get; init; }

    /// <summary>+1 per round within one <see cref="BootId"/>.</summary>
    public required long Round { get; init; }

    public required int Part { get; init; }

    public required int PartCount { get; init; }

    public required DateTimeOffset SentUtc
    {
        get;
        init => field = ContractRules.TruncateToMilliseconds(value);
    }

    /// <summary>The Edge's own command switch (<c>Edge:Commands:Enabled</c>).</summary>
    public required bool CommandsEnabled { get; init; }

    /// <summary>Status entries; null on a part that carries only tags.</summary>
    public IReadOnlyList<InventoryDevice>? Devices { get; init; }

    /// <summary>Tags of one PLC; null on a part that carries only status entries.</summary>
    public InventoryTags? Tags { get; init; }

    public void Validate()
    {
        ContractRules.SchemaVersion(SchemaVersion);
        ContractRules.EdgeId(EdgeId);
        ContractRules.NonEmptyGuid(BootId, "bootId");

        if (Round < 0)
        {
            throw new ContractValidationException("round", "must be >= 0");
        }

        if (PartCount is < 1 or > MaxParts)
        {
            throw new ContractValidationException("partCount", $"must be within 1..{MaxParts}");
        }

        if (Part < 0 || Part >= PartCount)
        {
            throw new ContractValidationException("part", "must be within 0..partCount-1");
        }

        ContractRules.Utc(SentUtc, "sentUtc");

        if (Devices is null && Tags is null)
        {
            throw new ContractValidationException("devices", "a part must carry devices, tags or both");
        }

        if (Devices is not null)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in Devices)
            {
                if (device is null)
                {
                    throw new ContractValidationException("devices", "must not contain null");
                }

                device.Validate();
                if (!names.Add(device.Name))
                {
                    throw new ContractValidationException("devices", $"device names must be unique (ignoring case): {device.Name}");
                }
            }
        }

        Tags?.Validate();
    }
}

/// <summary>Wire values <c>disabled</c>, <c>waiting</c>, <c>connected</c>, <c>unavailable</c>.</summary>
public enum InventoryDeviceState
{
    Disabled,
    Waiting,
    Connected,
    Unavailable,
}

/// <summary>A fixed error code (never driver text): <c>unavailable</c> or <c>tagErrors</c>.</summary>
public enum InventoryDeviceError
{
    Unavailable,
    TagErrors,
}

/// <summary>One PLC as the Edge reports it.</summary>
public sealed record InventoryDevice
{
    public required string Name { get; init; }

    /// <summary>The model name (<c>S7-1200</c>, <c>FX5U</c>, <c>FX3U</c>).</summary>
    public required string Model { get; init; }

    public required bool Enabled { get; init; }

    public required bool WritesEnabled { get; init; }

    public required InventoryDeviceState State { get; init; }

    /// <summary>The last cycle that reached the PLC; null when none has yet.</summary>
    public required DateTimeOffset? LastGoodUtc
    {
        get;
        init => field = value is { } time ? ContractRules.TruncateToMilliseconds(time) : null;
    }

    public required InventoryDeviceError? LastError { get; init; }

    public required int TagCount { get; init; }

    internal void Validate()
    {
        InventoryRules.Name(Name, "name");
        InventoryRules.Name(Model, "model");
        ContractRules.Defined(State, "state");
        if (LastError is { } error)
        {
            ContractRules.Defined(error, "lastError");
        }

        if (LastGoodUtc is { } lastGood)
        {
            ContractRules.Utc(lastGood, "lastGoodUtc");
        }

        if (TagCount < 0)
        {
            throw new ContractValidationException("tagCount", "must be >= 0");
        }
    }
}

/// <summary>Tags of one PLC, in the Edge's configuration order.</summary>
public sealed record InventoryTags
{
    public required string Device { get; init; }

    public required IReadOnlyList<InventoryTag> Items { get; init; }

    internal void Validate()
    {
        InventoryRules.Name(Device, "device");
        if (Items is null)
        {
            throw new ContractValidationException("items", "is required (may be empty)");
        }

        foreach (var item in Items)
        {
            if (item is null)
            {
                throw new ContractValidationException("items", "must not contain null");
            }

            item.Validate();
        }
    }
}

/// <summary>One tag's description as the Edge configures it; values travel as telemetry.</summary>
public sealed record InventoryTag
{
    public const int MaxDescriptionLength = 256;

    public const int MaxLength = 256;

    public required string TagId { get; init; }

    /// <summary>The tag's name within its PLC (the TagId itself on a migrated single-PLC Edge).</summary>
    public required string Name { get; init; }

    /// <summary>A PLC data type name such as <c>Int</c> or <c>LReal</c>; the Edge only sends names it knows.</summary>
    public required string DataType { get; init; }

    /// <summary>Characters of a <c>String</c> tag.</summary>
    public required int? Length { get; init; }

    public required bool Writable { get; init; }

    public required decimal? Min { get; init; }

    public required decimal? Max { get; init; }

    public required string? Description { get; init; }

    internal void Validate()
    {
        ContractRules.TagId(TagId);
        InventoryRules.TagName(Name);
        InventoryRules.DataType(DataType);

        if (Length is < 1 or > MaxLength)
        {
            throw new ContractValidationException("length", $"must be within 1..{MaxLength}");
        }

        if (Min is { } min && Max is { } max && min > max)
        {
            throw new ContractValidationException("min", "must not be greater than max");
        }

        if (Description is not null)
        {
            ContractRules.PlainText(Description, "description", MaxDescriptionLength);
        }
    }
}

/// <summary>Name rules of the inventory, restated from the Edge's configuration rules (Shared stays PLC-free).</summary>
internal static partial class InventoryRules
{
    // `\z` (not `$`): `$` also matches before a trailing "\n".
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagNamePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,15}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DataTypePattern();

    public static void Name(string? value, string field) => Match(NamePattern(), value, field, "1..32 chars [A-Za-z0-9_-]");

    public static void TagName(string? value) => Match(TagNamePattern(), value, "name", "1..64 chars [A-Za-z0-9_.-]");

    public static void DataType(string? value) => Match(DataTypePattern(), value, "dataType", "a type name of 1..16 letters and digits");

    private static void Match(Regex pattern, string? value, string field, string rule)
    {
        if (value is null || !pattern.IsMatch(value))
        {
            throw new ContractValidationException(field, $"must be {rule}");
        }
    }
}
