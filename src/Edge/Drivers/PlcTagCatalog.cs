using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Edge.Drivers;

/// <summary>Tags known to this Edge; bound from configuration section <c>Edge:Tags</c>.</summary>
public sealed class PlcTagCatalogOptions
{
    public PlcTagDefinition[] Tags { get; set; } = [];
}

/// <summary>A catalog tag whose address has been parsed and checked against its data type.</summary>
public sealed record ResolvedTag(
    string TagId,
    PlcAddress Address,
    PlcDataType DataType,
    bool Writable,
    decimal? Min,
    decimal? Max);

/// <summary>
/// Outcome of <see cref="PlcTagCatalog.EvaluateWrite"/>. When <see cref="Allowed"/> is false,
/// <see cref="Reason"/> holds a <see cref="CommandReasons"/> code.
/// </summary>
public sealed record WriteDecision(bool Allowed, string? Reason, ResolvedTag? ResolvedTag, object? NormalizedValue)
{
    internal static WriteDecision Deny(string reason, ResolvedTag? tag = null) => new(false, reason, tag, null);

    internal static WriteDecision Allow(ResolvedTag tag, object value) => new(true, null, tag, value);
}

/// <summary>
/// The Edge's last line of defense for writes: a command can only touch a tag that is configured here,
/// marked writable, and given a value that fits its type and bounds. All checks run before any PLC I/O,
/// and an invalid catalog stops startup instead of running with a partially trusted configuration.
/// </summary>
public sealed partial class PlcTagCatalog
{
    private readonly Dictionary<string, ResolvedTag> _tags = new(StringComparer.OrdinalIgnoreCase);

    public PlcTagCatalog(PlcTagCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var definition in options.Tags ?? [])
        {
            var tag = Resolve(definition);
            if (!_tags.TryAdd(tag.TagId, tag))
            {
                throw new InvalidOperationException($"Tag catalog: duplicate tagId '{tag.TagId}' (ids are case-insensitive).");
            }
        }

        EnsureWritableTagsDoNotOverlap();
    }

    public bool TryGet(string tagId, [NotNullWhen(true)] out ResolvedTag? tag)
    {
        tag = null;
        return tagId is not null && _tags.TryGetValue(tagId, out tag);
    }

    /// <summary>Decides whether <paramref name="targetValue"/> may be written to <paramref name="tagId"/>.</summary>
    public WriteDecision EvaluateWrite(string tagId, JsonElement targetValue)
    {
        if (!TryGet(tagId, out var tag))
        {
            return WriteDecision.Deny(CommandReasons.UnknownTag);
        }

        if (tag.Address.Area == PlcArea.Input)
        {
            return WriteDecision.Deny(CommandReasons.InputAreaNotWritable, tag);
        }

        if (!tag.Writable)
        {
            return WriteDecision.Deny(CommandReasons.TagNotWritable, tag);
        }

        object value;
        try
        {
            value = PlcValueConverter.Normalize(tag.DataType, targetValue);
        }
        catch (PlcValueException)
        {
            return WriteDecision.Deny(CommandReasons.InvalidValue, tag);
        }

        return IsWithinBounds(tag, value)
            ? WriteDecision.Allow(tag, value)
            : WriteDecision.Deny(CommandReasons.ValueOutOfRange, tag);
    }

    private static bool IsWithinBounds(ResolvedTag tag, object value)
    {
        if (value is float real)
        {
            // Compare in float precision: a bound of 0.1 must admit the float 0.1f the sender meant,
            // which is slightly larger than the decimal 0.1.
            return (tag.Min is not { } min || real >= (float)min)
                && (tag.Max is not { } max || real <= (float)max);
        }

        if (value is bool)
        {
            return true;
        }

        var number = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        return (tag.Min is not { } lower || number >= lower)
            && (tag.Max is not { } upper || number <= upper);
    }

    private static ResolvedTag Resolve(PlcTagDefinition? definition)
    {
        if (definition is null)
        {
            throw new InvalidOperationException("Tag catalog: empty tag entry.");
        }

        if (definition.TagId is null || !TagIdPattern().IsMatch(definition.TagId))
        {
            throw Invalid(definition, "tagId must be 1..64 chars [A-Za-z0-9_.-]");
        }

        PlcAddress address;
        try
        {
            address = PlcAddressParser.Parse(definition.Address);
            PlcValueConverter.EnsureCompatible(address, definition.DataType);
        }
        catch (PlcException ex)
        {
            throw Invalid(definition, ex.Message, ex);
        }

        if (definition.Writable && address.Area == PlcArea.Input)
        {
            throw Invalid(definition, "input area (I) tags cannot be writable");
        }

        ValidateBounds(definition);

        return new ResolvedTag(definition.TagId, address, definition.DataType, definition.Writable, definition.Min, definition.Max);
    }

    private static void ValidateBounds(PlcTagDefinition definition)
    {
        if (definition.Min is null && definition.Max is null)
        {
            return;
        }

        if (definition.DataType == PlcDataType.Bool)
        {
            throw Invalid(definition, "Min/Max are not allowed on Bool tags");
        }

        if (definition.Min > definition.Max)
        {
            throw Invalid(definition, "Min must not be greater than Max");
        }

        // Real's range (±3.4e38) is wider than decimal's (±7.9e28), so only integer types can overflow.
        if (PlcValueConverter.IntegerRange(definition.DataType) is { } range
            && (definition.Min < range.Min || definition.Min > range.Max || definition.Max < range.Min || definition.Max > range.Max))
        {
            throw Invalid(definition, $"Min/Max must be within the {definition.DataType} range {range.Min}..{range.Max}");
        }
    }

    /// <summary>
    /// A writable tag must own its bytes: if another tag shares them, writing one silently changes the
    /// other (e.g. writing DB1.DBW0 overwrites the read-only DB1.DBX0.3). Bits of the same byte are
    /// independent, so two Bool tags on different bits do not overlap.
    /// </summary>
    private void EnsureWritableTagsDoNotOverlap()
    {
        var tags = _tags.Values.ToArray();
        foreach (var writable in tags.Where(t => t.Writable))
        {
            foreach (var other in tags)
            {
                if (!ReferenceEquals(writable, other) && Overlaps(writable.Address, other.Address))
                {
                    throw new InvalidOperationException(
                        $"Tag catalog: writable tag '{writable.TagId}' ({writable.Address}) overlaps tag '{other.TagId}' ({other.Address}).");
                }
            }
        }
    }

    private static bool Overlaps(PlcAddress a, PlcAddress b)
    {
        if (a.Area != b.Area || a.DbNumber != b.DbNumber)
        {
            return false;
        }

        if (a.IsBit && b.IsBit && a.ByteOffset == b.ByteOffset)
        {
            return a.BitOffset == b.BitOffset;
        }

        var aEnd = a.ByteOffset + Math.Max(a.SizeBytes, 1);
        var bEnd = b.ByteOffset + Math.Max(b.SizeBytes, 1);
        return a.ByteOffset < bEnd && b.ByteOffset < aEnd;
    }

    private static InvalidOperationException Invalid(PlcTagDefinition definition, string reason, Exception? inner = null) =>
        new($"Tag catalog: tag '{definition.TagId}' is invalid: {reason}.", inner);

    // Same rule as the contract tagId, so every catalog tag can appear in telemetry and commands.
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagIdPattern();
}
