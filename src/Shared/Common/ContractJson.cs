using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IndustrialIot.Shared.Common;

/// <summary>Implemented by every wire contract so <see cref="ContractJson"/> can validate it.</summary>
public interface IContract
{
    /// <summary>Throws <see cref="ContractValidationException"/> when any field breaks its rule.</summary>
    void Validate();
}

/// <summary>
/// The single JSON configuration shared by Edge and Cloud. Always (de)serialize contracts through
/// these helpers so both sides produce and accept exactly the same wire format.
/// </summary>
public static class ContractJson
{
    public const int SchemaVersion = 1;

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Validates, then serializes. Producers can never emit an invalid message.</summary>
    public static string Serialize<T>(T contract)
        where T : IContract
    {
        ArgumentNullException.ThrowIfNull(contract);
        contract.Validate();
        try
        {
            return JsonSerializer.Serialize(contract, Options);
        }
        catch (JsonException ex)
        {
            throw new ContractValidationException(ex.Path, ex.Message, ex);
        }
    }

    /// <summary>Deserializes and validates. Any failure surfaces as <see cref="ContractValidationException"/>.</summary>
    public static T Deserialize<T>(string json)
        where T : IContract
    {
        T? contract;
        try
        {
            contract = JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException ex)
        {
            // Covers malformed JSON, wrong token kinds, bad GUIDs/enums/timestamps and missing `required` members.
            throw new ContractValidationException(ex.Path, ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            // Includes a null message and invalid UTF-16 such as a lone surrogate escape ("\uD800").
            throw new ContractValidationException(field: null, ex.Message, ex);
        }

        if (contract is null)
        {
            throw new ContractValidationException(field: null, "message is the JSON literal null");
        }

        contract.Validate();
        return contract;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Forward compatibility: fields added by a newer sender are ignored, not rejected.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            // Numbers must be JSON numbers ("12" as a string is rejected).
            NumberHandling = JsonNumberHandling.Strict,
            // {"edgeId":"a","edgeId":"b"} is rejected: two parsers must never disagree on which value wins.
            AllowDuplicateProperties = false,
        };
        options.Converters.Add(new StrictEnumConverterFactory());
        options.Converters.Add(new UtcTimestampConverter());
        // Freeze the shared instance so no caller can mutate the wire format at runtime.
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// Reads/writes timestamps as ISO-8601 UTC with a mandatory <c>Z</c>, e.g. <c>2026-10-13T08:00:00.000Z</c>.
    /// The default converter would silently treat a value without offset as server-local time.
    /// </summary>
    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        private const string WriteFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        // Exact shapes only: seconds required, 0 or 1-3 fractional digits (ms precision), literal 'Z'.
        // ("FFF" would also accept a bare ".Z", so each digit count is listed explicitly.)
        private static readonly string[] ReadFormats =
        [
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss'.'f'Z'",
            "yyyy-MM-dd'T'HH:mm:ss'.'ff'Z'",
            "yyyy-MM-dd'T'HH:mm:ss'.'fff'Z'",
        ];

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("timestamp must be a string");
            }

            var text = reader.GetString()!;
            if (!DateTimeOffset.TryParseExact(
                    text,
                    ReadFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var value))
            {
                throw new JsonException(
                    $"timestamp '{text}' must be ISO-8601 UTC ending with 'Z', e.g. 2026-10-13T08:00:00.000Z");
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString(WriteFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Enums as exact camelCase names only. The built-in JsonStringEnumConverter is too lenient for a
    /// wire contract: it ignores case and parses "rejected, expired" as a flags combination.
    /// </summary>
    private sealed class StrictEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(StrictEnumConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class StrictEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly Dictionary<string, TEnum> ByName =
            Enum.GetValues<TEnum>().ToDictionary(value => ToCamelCase(value.ToString()), StringComparer.Ordinal);

        private static readonly Dictionary<TEnum, string> ByValue =
            ByName.ToDictionary(pair => pair.Value, pair => pair.Key);

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"{typeof(TEnum).Name} must be a string");
            }

            var text = reader.GetString()!;
            return ByName.TryGetValue(text, out var value)
                ? value
                : throw new JsonException($"'{text}' is not a valid {typeof(TEnum).Name}; expected one of: {string.Join(", ", ByName.Keys)}");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (!ByValue.TryGetValue(value, out var name))
            {
                throw new JsonException($"{typeof(TEnum).Name} value {value} is not defined");
            }

            writer.WriteStringValue(name);
        }

        private static string ToCamelCase(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
    }
}

/// <summary>Field rules shared by the contract records.</summary>
internal static partial class ContractRules
{
    // `\z` (not `$`): in .NET `$` also matches before a trailing "\n", which would let "edge01\n" through.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EdgeIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagIdPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonPattern();

    public static void SchemaVersion(int value)
    {
        if (value != ContractJson.SchemaVersion)
        {
            throw new ContractValidationException("schemaVersion", $"unsupported version {value}; expected {ContractJson.SchemaVersion}");
        }
    }

    public static void EdgeId(string? value) => Match(EdgeIdPattern(), value, "edgeId", "1..64 chars [A-Za-z0-9_-]");

    public static void TagId(string? value) => Match(TagIdPattern(), value, "tagId", "1..64 chars [A-Za-z0-9_.-]");

    public static void Reason(string? value)
    {
        if (value is not null)
        {
            Match(ReasonPattern(), value, "reason", "snake_case code matching ^[a-z][a-z0-9_]{0,63}$");
        }
    }

    public static void NonEmptyGuid(Guid value, string field)
    {
        if (value == Guid.Empty)
        {
            throw new ContractValidationException(field, "must be a non-empty GUID");
        }
    }

    public static void Utc(DateTimeOffset value, string field)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ContractValidationException(field, "must be a UTC timestamp");
        }
    }

    /// <summary>A command/telemetry value: JSON true, false or a finite number.</summary>
    public static void ScalarValue(JsonElement value, string field)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True or JsonValueKind.False:
                return;
            // 1e400 is valid JSON but parses to Infinity; non-finite values are forbidden by the contract.
            case JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number):
                return;
            case JsonValueKind.Number:
                throw new ContractValidationException(field, "must be a finite number");
            default:
                throw new ContractValidationException(field, $"must be true, false or a number (got {value.ValueKind})");
        }
    }

    public static void Defined<TEnum>(TEnum value, string field)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ContractValidationException(field, $"{value} is not a defined {typeof(TEnum).Name}");
        }
    }

    /// <summary>Free text such as <c>issuedBy</c>: 1..max chars, not blank, no control chars, no lone surrogates.</summary>
    public static void PlainText(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ContractValidationException(field, $"must be 1..{maxLength} characters and not blank");
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c))
            {
                throw new ContractValidationException(field, "must not contain control characters");
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++; // valid surrogate pair (e.g. an emoji)
                continue;
            }

            if (char.IsSurrogate(c))
            {
                // A lone surrogate cannot be encoded as UTF-8 and would be silently replaced on the wire.
                throw new ContractValidationException(field, "must be valid UTF-16 text");
            }
        }
    }

    /// <summary>JSON null and default(JsonElement) become C# null, so there is a single "no value" state.</summary>
    public static JsonElement? NormalizeValue(JsonElement? value) =>
        value is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } ? value : null;

    /// <summary>The wire format carries milliseconds; dropping sub-ms ticks keeps Validate and round-trips consistent.</summary>
    public static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Offset);

    public static bool RawEquals(JsonElement? left, JsonElement? right)
    {
        var leftText = RawTextOrNull(left);
        var rightText = RawTextOrNull(right);
        return string.Equals(leftText, rightText, StringComparison.Ordinal);
    }

    public static int RawHash(JsonElement? value) =>
        RawTextOrNull(value) is { } text ? StringComparer.Ordinal.GetHashCode(text) : 0;

    // C# null, JSON null and default(JsonElement) all mean "no value".
    private static string? RawTextOrNull(JsonElement? value) =>
        value is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element ? element.GetRawText() : null;

    private static void Match(Regex pattern, string? value, string field, string rule)
    {
        if (value is null || !pattern.IsMatch(value))
        {
            throw new ContractValidationException(field, $"must be {rule}");
        }
    }
}
