using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using IndustrialIot.Edge.Drivers.Mc1e;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Slmp;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Edge.Drivers;

/// <summary>Tags known to this Edge; bound from configuration section <c>Edge:Tags</c>.</summary>
public sealed class PlcTagCatalogOptions
{
    public PlcTagDefinition[] Tags { get; set; } = [];
}

/// <summary>
/// A catalog tag whose address has been parsed and checked against its data type. <see cref="Length"/>: characters of
/// a String tag, null for every other type.
/// </summary>
public sealed record ResolvedTag(
    string TagId,
    IPlcAddress Address,
    PlcDataType DataType,
    bool Writable,
    decimal? Min,
    decimal? Max,
    int? Length = null);

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
        : this(options, new S7AddressSyntax())
    {
    }

    public PlcTagCatalog(PlcTagCatalogOptions options, IPlcAddressSyntax syntax)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(syntax);

        var ordered = new List<ResolvedTag>();
        foreach (var definition in options.Tags ?? [])
        {
            var tag = Resolve(definition, syntax);
            if (!_tags.TryAdd(tag.TagId, tag))
            {
                throw new InvalidOperationException($"Tag catalog: duplicate tagId '{tag.TagId}' (ids are case-insensitive).");
            }

            ordered.Add(tag);
        }

        Tags = ordered.ToArray();
        EnsureWritableTagsDoNotOverlap();
    }

    private PlcTagCatalog(IReadOnlyList<ResolvedTag> tags)
    {
        foreach (var tag in tags)
        {
            if (!_tags.TryAdd(tag.TagId, tag))
            {
                throw new InvalidOperationException($"Tag catalog: duplicate tagId '{tag.TagId}' (ids are case-insensitive).");
            }
        }

        Tags = [.. tags];
    }

    /// <summary>
    /// Several PLCs' catalogs as one view, in the given order. Each was checked when it was built; writable overlap
    /// is not checked again, because it is a rule within one PLC and two PLCs may use the same address.
    /// </summary>
    public static PlcTagCatalog Union(IEnumerable<PlcTagCatalog> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        return new PlcTagCatalog([.. catalogs.SelectMany(catalog => catalog.Tags)]);
    }

    /// <summary>All tags in configuration order (the order the poller reads them).</summary>
    public IReadOnlyList<ResolvedTag> Tags { get; }

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

        if (tag.Address.ReadOnlyReason is not null)
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

        if (tag.DataType == PlcDataType.String)
        {
            // One byte per character on the PLC, and never more than the tag's Length: a longer text would overwrite
            // whatever follows. Padding with NULs clears the rest of a Mitsubishi string's registers.
            var text = (string)value;
            if (tag.Length is not { } length || text.Length > length || text.Any(c => c > '\u00FF'))
            {
                return WriteDecision.Deny(CommandReasons.InvalidValue, tag);
            }

            value = text.PadRight(length, '\0');
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

        if (value is double lreal)
        {
            // An LReal can exceed decimal's range (1e300), so compare as double.
            return (tag.Min is not { } min || lreal >= (double)min)
                && (tag.Max is not { } max || lreal <= (double)max);
        }

        if (value is bool or string or DateTime)
        {
            return true;
        }

        var number = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        return (tag.Min is not { } lower || number >= lower)
            && (tag.Max is not { } upper || number <= upper);
    }

    private static ResolvedTag Resolve(PlcTagDefinition? definition, IPlcAddressSyntax syntax)
    {
        if (definition is null)
        {
            throw new InvalidOperationException("Tag catalog: empty tag entry.");
        }

        if (definition.TagId is null || !TagIdPattern().IsMatch(definition.TagId))
        {
            throw Invalid(definition, "tagId must be 1..64 chars [A-Za-z0-9_.-]");
        }

        if (definition.DataType == PlcDataType.String ? definition.Length is not > 0 : definition.Length is not null)
        {
            throw Invalid(definition, definition.DataType == PlcDataType.String
                ? "String tags need Length (characters, at least 1)"
                : "Length applies to String tags only");
        }

        IPlcAddress address;
        try
        {
            address = syntax.Parse(definition.Address, definition.DataType, definition.Length);
        }
        catch (PlcException ex)
        {
            throw Invalid(definition, ex.Message, ex);
        }

        if (definition.Writable && address.ReadOnlyReason is { } readOnlyReason)
        {
            throw Invalid(definition, readOnlyReason);
        }

        ValidateBounds(definition);

        return new ResolvedTag(
            definition.TagId, address, definition.DataType, definition.Writable, definition.Min, definition.Max, definition.Length);
    }

    private static void ValidateBounds(PlcTagDefinition definition)
    {
        if (definition.Min is null && definition.Max is null)
        {
            return;
        }

        if (definition.DataType is PlcDataType.Bool or PlcDataType.String or PlcDataType.DTL)
        {
            throw Invalid(definition, $"Min/Max are not allowed on {definition.DataType} tags");
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
            var mine = Footprint(writable)
                ?? throw new InvalidOperationException(
                    $"Tag catalog: writable tag '{writable.TagId}' has an address the overlap check does not know ({writable.Address.Canonical}).");

            foreach (var other in tags)
            {
                if (ReferenceEquals(writable, other))
                {
                    continue;
                }

                var theirs = Footprint(other)
                    ?? throw new InvalidOperationException(
                        $"Tag catalog: tag '{other.TagId}' has an address the overlap check does not know ({other.Address.Canonical}).");

                if (mine.Space == theirs.Space && mine.Start < theirs.End && theirs.Start < mine.End)
                {
                    throw new InvalidOperationException(
                        $"Tag catalog: writable tag '{writable.TagId}' ({writable.Address}) overlaps tag '{other.TagId}' ({other.Address}).");
                }
            }
        }
    }

    /// <summary>
    /// The memory a tag occupies, with its real size: S7 in bits of one area and DB (a bit is one bit, anything else
    /// its whole item: String Length + 2 bytes, LReal 8, DTL 12); Mitsubishi D in registers (String ceil(Length/2),
    /// LReal 4, DInt/DWord/Real 2) and bit devices in points. Null for an address of another kind.
    /// </summary>
    private static (string Space, long Start, long End)? Footprint(ResolvedTag tag) => tag.Address switch
    {
        PlcAddress { IsBit: true } s7 => ($"S7:{s7.Area}:{s7.DbNumber}", (s7.ByteOffset * 8L) + s7.BitOffset!.Value, (s7.ByteOffset * 8L) + s7.BitOffset!.Value + 1),
        PlcAddress s7 => ($"S7:{s7.Area}:{s7.DbNumber}", s7.ByteOffset * 8L, (s7.ByteOffset + PlcValueConverter.S7ItemSize(tag.DataType, tag.Length)) * 8L),
        SlmpAddress { IsBit: true } bit => ($"SLMP:{bit.Device}", bit.Number, bit.Number + 1),
        SlmpAddress word => ($"SLMP:{word.Device}", word.Number, word.Number + SlmpBlockPlanner.WordCount(tag.DataType, tag.Length)),
        Mc1eAddress { IsBit: true } bit => ($"MC1E:{bit.Device}", bit.Number, bit.Number + 1),
        Mc1eAddress word => ($"MC1E:{word.Device}", word.Number, word.Number + SlmpBlockPlanner.WordCount(tag.DataType, tag.Length)),
        _ => null,
    };

    private static InvalidOperationException Invalid(PlcTagDefinition definition, string reason, Exception? inner = null) =>
        new($"Tag catalog: tag '{definition.TagId}' is invalid: {reason}.", inner);

    // Same rule as the contract tagId, so every catalog tag can appear in telemetry and commands.
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagIdPattern();
}
