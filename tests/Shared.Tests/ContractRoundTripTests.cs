using System.Text.Json;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;

namespace IndustrialIot.Shared.Tests;

public class ContractRoundTripTests
{
    // Snapshot JSON: the exact wire format agreed between Edge and Cloud (field names and order).
    private const string TelemetryJson =
        """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":42,"tagId":"pump1.pressure","value":3.5,"quality":"good","timestampUtc":"2026-10-13T08:00:00.123Z"}""";

    private const string CommandJson =
        """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"pump1.speedSetpoint","targetValue":1500,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"operator@cloud"}""";

    private const string ResultJson =
        """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","outcome":"rejected","reason":"tag_not_writable","completedAtUtc":"2026-10-13T08:00:01.500Z"}""";

    internal static TelemetryPayload SampleTelemetry() => new()
    {
        SchemaVersion = 1,
        EdgeId = "edge-01",
        StreamId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
        Sequence = 42,
        TagId = "pump1.pressure",
        Value = JsonSerializer.SerializeToElement(3.5),
        Quality = TelemetryQuality.Good,
        TimestampUtc = new DateTimeOffset(2026, 10, 13, 8, 0, 0, 123, TimeSpan.Zero),
    };

    internal static DeviceCommand SampleCommand() => new()
    {
        SchemaVersion = 1,
        CommandId = Guid.Parse("5d1c2a3b-0000-4000-8000-000000000001"),
        EdgeId = "edge-01",
        TagId = "pump1.speedSetpoint",
        TargetValue = JsonSerializer.SerializeToElement(1500),
        IssuedAtUtc = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero),
        ExpiresAtUtc = new DateTimeOffset(2026, 10, 13, 8, 5, 0, TimeSpan.Zero),
        IssuedBy = "operator@cloud",
    };

    internal static CommandResult SampleResult() => new()
    {
        SchemaVersion = 1,
        CommandId = Guid.Parse("5d1c2a3b-0000-4000-8000-000000000001"),
        EdgeId = "edge-01",
        Outcome = CommandOutcome.Rejected,
        Reason = CommandReasons.TagNotWritable,
        CompletedAtUtc = new DateTimeOffset(2026, 10, 13, 8, 0, 1, 500, TimeSpan.Zero),
    };

    [Fact]
    public void RoundTrip_Equal()
    {
        var telemetry = SampleTelemetry();
        var command = SampleCommand();
        var result = SampleResult();

        Assert.Equal(telemetry, ContractJson.Deserialize<TelemetryPayload>(ContractJson.Serialize(telemetry)));
        Assert.Equal(command, ContractJson.Deserialize<DeviceCommand>(ContractJson.Serialize(command)));
        Assert.Equal(result, ContractJson.Deserialize<CommandResult>(ContractJson.Serialize(result)));

        // Telemetry with a non-good quality carries a null value and must also round-trip.
        var badSample = telemetry with { Value = null, Quality = TelemetryQuality.Bad };
        Assert.Equal(badSample, ContractJson.Deserialize<TelemetryPayload>(ContractJson.Serialize(badSample)));
    }

    [Fact]
    public void FieldNames_MatchSchema()
    {
        Assert.Equal(TelemetryJson, ContractJson.Serialize(SampleTelemetry()));
        Assert.Equal(CommandJson, ContractJson.Serialize(SampleCommand()));
        Assert.Equal(ResultJson, ContractJson.Serialize(SampleResult()));

        // The snapshot text is also accepted as input and equals the C# sample.
        Assert.Equal(SampleTelemetry(), ContractJson.Deserialize<TelemetryPayload>(TelemetryJson));
        Assert.Equal(SampleCommand(), ContractJson.Deserialize<DeviceCommand>(CommandJson));
        Assert.Equal(SampleResult(), ContractJson.Deserialize<CommandResult>(ResultJson));
    }

    [Theory]
    // DeviceCommand
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z"}""")] // missing issuedBy
    [InlineData("command", """{"schemaVersion":2,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")]
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:00:00.000Z","issuedBy":"u"}""")] // expires == issued
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-14T08:00:00.001Z","issuedBy":"u"}""")] // lifetime > 24 h
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000+07:00","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")] // +07:00 offset
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")] // no offset
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":"1500","issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")] // string targetValue
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":null,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")]
    [InlineData("command", """{"schemaVersion":1,"commandId":"00000000-0000-0000-0000-000000000000","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")]
    [InlineData("command", """{"schemaVersion":1,"commandId":"not-a-guid","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")]
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge 01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")]
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01\n","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")] // trailing newline
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":""}""")]
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"   "}""")] // blank
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"op\u0007"}""")] // control char
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"e\uD800","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.000Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")] // lone surrogate
    [InlineData("command", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","tagId":"t","targetValue":1,"issuedAtUtc":"2026-10-13T08:00:00.Z","expiresAtUtc":"2026-10-13T08:05:00.000Z","issuedBy":"u"}""")] // dot without digits
    // TelemetryPayload
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"not-a-guid","sequence":1,"tagId":"t","value":1,"quality":"good","timestampUtc":"2026-10-13T08:00:00.000Z"}""")]
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":-1,"tagId":"t","value":1,"quality":"good","timestampUtc":"2026-10-13T08:00:00.000Z"}""")]
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":null,"quality":"good","timestampUtc":"2026-10-13T08:00:00.000Z"}""")] // good needs a value
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":1,"quality":"bad","timestampUtc":"2026-10-13T08:00:00.000Z"}""")] // bad must be null
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":1,"quality":"excellent","timestampUtc":"2026-10-13T08:00:00.000Z"}""")]
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":{"x":1},"quality":"good","timestampUtc":"2026-10-13T08:00:00.000Z"}""")]
    // CommandResult
    [InlineData("result", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","outcome":"rejected","reason":"Tag Not Writable","completedAtUtc":"2026-10-13T08:00:00.000Z"}""")]
    [InlineData("result", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","outcome":"done","completedAtUtc":"2026-10-13T08:00:00.000Z"}""")]
    [InlineData("result", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","outcome":0,"completedAtUtc":"2026-10-13T08:00:00.000Z"}""")] // enum as integer
    [InlineData("result", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","outcome":"rejected, expired","completedAtUtc":"2026-10-13T08:00:00.000Z"}""")] // flags syntax
    [InlineData("result", """{"schemaVersion":1,"commandId":"5d1c2a3b-0000-4000-8000-000000000001","edgeId":"edge-01","outcome":"Applied","completedAtUtc":"2026-10-13T08:00:00.000Z"}""")] // wrong case
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":null,"quality":"bad, uncertain","timestampUtc":"2026-10-13T08:00:00.000Z"}""")]
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":1e400,"quality":"good","timestampUtc":"2026-10-13T08:00:00.000Z"}""")] // overflows to Infinity
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":1,"quality":"good","timestampUtc":"2026-10-13T08:00:00.1234567Z"}""")] // > ms precision
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":1,"quality":"good","timestampUtc":" 2026-10-13T08:00:00Z"}""")] // leading space
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":1,"quality":"good","timestampUtc":"2026-10-13T08:00Z"}""")] // no seconds
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":1,"quality":"good","timestampUtc":"2026-10-13T08:00:00+07:00Z"}""")]
    [InlineData("telemetry", """{"schemaVersion":1,"edgeId":"edge-01","edgeId":"edge-02","streamId":"0f8fad5b-d9cb-469f-a165-70867728950e","sequence":1,"tagId":"t","value":1,"quality":"good","timestampUtc":"2026-10-13T08:00:00.000Z"}""")] // duplicate property
    // Not even an object
    [InlineData("result", "{")]
    [InlineData("result", "null")]
    public void Invalid_ThrowsValidation(string kind, string json)
    {
        // Only ContractValidationException may escape: never a raw JsonException.
        Assert.Throws<ContractValidationException>(() => Deserialize(kind, json));
    }

    [Fact]
    public void UnknownFields_AndUnknownReasonCodes_AreAccepted()
    {
        var withExtraField = CommandJson.Replace("\"issuedBy\"", "\"priority\":\"high\",\"issuedBy\"", StringComparison.Ordinal);
        Assert.Equal(SampleCommand(), ContractJson.Deserialize<DeviceCommand>(withExtraField));

        var futureReason = ResultJson.Replace("tag_not_writable", "some_future_code", StringComparison.Ordinal);
        Assert.Equal("some_future_code", ContractJson.Deserialize<CommandResult>(futureReason).Reason);

        // Unknown fields are skipped without being decoded, so even invalid text inside one cannot
        // break the message (the same lone surrogate in a known field is rejected: see Invalid_ThrowsValidation).
        var invalidTextInUnknownField = CommandJson.Replace("\"issuedBy\"", "\"x\":\"\\uD800\",\"issuedBy\"", StringComparison.Ordinal);
        Assert.Equal(SampleCommand(), ContractJson.Deserialize<DeviceCommand>(invalidTextInUnknownField));
    }

    [Fact]
    public void Serialize_InvalidContract_Throws()
    {
        var invalid = SampleCommand() with { ExpiresAtUtc = SampleCommand().IssuedAtUtc };
        Assert.Throws<ContractValidationException>(() => ContractJson.Serialize(invalid));

        // An undefined enum value assigned in code must not leak a raw JsonException.
        var undefinedOutcome = SampleResult() with { Outcome = (CommandOutcome)42 };
        Assert.Throws<ContractValidationException>(() => ContractJson.Serialize(undefinedOutcome));

        // A lone surrogate would be silently replaced by U+FFFD on the wire, so it is rejected up front.
        var loneSurrogate = SampleCommand() with { IssuedBy = "user\uD800" };
        Assert.Throws<ContractValidationException>(() => ContractJson.Serialize(loneSurrogate));
    }

    [Fact]
    public void ValidTextAndValueEdgeCases_AreAccepted()
    {
        // A proper surrogate pair (emoji) is valid text.
        var emojiIssuer = SampleCommand() with { IssuedBy = "operator \U0001F600" };
        Assert.Equal(emojiIssuer, ContractJson.Deserialize<DeviceCommand>(ContractJson.Serialize(emojiIssuer)));

        // default(JsonElement) and JSON null both mean "no value" for a bad sample.
        var badWithDefault = SampleTelemetry() with { Value = default(JsonElement), Quality = TelemetryQuality.Bad };
        var badWithNull = SampleTelemetry() with { Value = null, Quality = TelemetryQuality.Bad };
        Assert.Null(badWithDefault.Value);
        Assert.Equal(badWithNull, badWithDefault);
        Assert.Equal(badWithNull, ContractJson.Deserialize<TelemetryPayload>(ContractJson.Serialize(badWithDefault)));

        // One and two fractional digits are valid ISO-8601 too.
        var oneDigit = CommandJson.Replace("08:00:00.000Z", "08:00:00.5Z", StringComparison.Ordinal);
        Assert.Equal(500, ContractJson.Deserialize<DeviceCommand>(oneDigit).IssuedAtUtc.Millisecond);
    }

    [Fact]
    public void SubMillisecondTimestamps_AreTruncated_AndRoundTripEqual()
    {
        // TimeProvider.GetUtcNow() often carries sub-ms ticks; the wire keeps ms only.
        var issued = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero).AddTicks(5_000); // +0.5 ms
        var command = SampleCommand() with { IssuedAtUtc = issued, ExpiresAtUtc = issued.AddTicks(4_000) }; // +0.9 ms

        // Both truncate to the same millisecond, so the producer rejects it instead of emitting a message
        // that every receiver would reject.
        Assert.Throws<ContractValidationException>(() => ContractJson.Serialize(command));

        var telemetry = SampleTelemetry() with { TimestampUtc = issued };
        Assert.Equal(telemetry, ContractJson.Deserialize<TelemetryPayload>(ContractJson.Serialize(telemetry)));
    }

    [Fact]
    public void TimestampWithoutZ_IsRejectedByTheUtcConverter()
    {
        // Independent of the machine time zone: the error must come from our 'Z' rule,
        // not from a local offset that happens to be non-zero on this machine.
        var noOffset = CommandJson.Replace("2026-10-13T08:00:00.000Z", "2026-10-13T08:00:00", StringComparison.Ordinal);

        var exception = Assert.Throws<ContractValidationException>(() => ContractJson.Deserialize<DeviceCommand>(noOffset));

        Assert.Contains("ending with 'Z'", exception.Message);
        Assert.Contains("issuedAtUtc", exception.Field);
    }

    private static object Deserialize(string kind, string json) => kind switch
    {
        "telemetry" => ContractJson.Deserialize<TelemetryPayload>(json),
        "command" => ContractJson.Deserialize<DeviceCommand>(json),
        "result" => ContractJson.Deserialize<CommandResult>(json),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
