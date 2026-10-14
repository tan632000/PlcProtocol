using System.Text.Json;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Shared.Contracts;

/// <summary>
/// One PLC tag sample sent from an Edge to the Cloud.
/// Dedupe key on the receiver: (<see cref="EdgeId"/>, <see cref="StreamId"/>, <see cref="Sequence"/>).
/// </summary>
public sealed record TelemetryPayload : IContract
{
    public required int SchemaVersion { get; init; }

    public required string EdgeId { get; init; }

    /// <summary>Generated once per Edge local store; a new value means the sequence restarted.</summary>
    public required Guid StreamId { get; init; }

    /// <summary>Monotonic within (<see cref="EdgeId"/>, <see cref="StreamId"/>).</summary>
    public required long Sequence { get; init; }

    public required string TagId { get; init; }

    /// <summary>JSON <c>true</c>/<c>false</c>/number when <see cref="Quality"/> is good; otherwise <c>null</c>.</summary>
    public required JsonElement? Value
    {
        get;
        init => field = ContractRules.NormalizeValue(value);
    }

    public required TelemetryQuality Quality { get; init; }

    /// <summary>Stored at millisecond precision (the wire precision).</summary>
    public required DateTimeOffset TimestampUtc
    {
        get;
        init => field = ContractRules.TruncateToMilliseconds(value);
    }

    public void Validate()
    {
        ContractRules.SchemaVersion(SchemaVersion);
        ContractRules.EdgeId(EdgeId);
        ContractRules.NonEmptyGuid(StreamId, "streamId");

        if (Sequence < 0)
        {
            throw new ContractValidationException("sequence", "must be >= 0");
        }

        ContractRules.TagId(TagId);
        ContractRules.Defined(Quality, "quality");

        // A null value is only meaningful when the sample is not good (e.g. PLC unreachable or NaN).
        // Value is already normalized: JSON null / default(JsonElement) are stored as C# null.
        if (Quality == TelemetryQuality.Good)
        {
            if (Value is not { } value)
            {
                throw new ContractValidationException("value", "must not be null when quality is good");
            }

            ContractRules.ScalarValue(value, "value");
        }
        else if (Value is not null)
        {
            throw new ContractValidationException("value", "must be null when quality is not good");
        }

        ContractRules.Utc(TimestampUtc, "timestampUtc");
    }

    // Records compare JsonElement by reference into its document; compare the JSON text instead.
    public bool Equals(TelemetryPayload? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && EdgeId == other.EdgeId
        && StreamId == other.StreamId
        && Sequence == other.Sequence
        && TagId == other.TagId
        && ContractRules.RawEquals(Value, other.Value)
        && Quality == other.Quality
        && TimestampUtc == other.TimestampUtc;

    public override int GetHashCode() =>
        HashCode.Combine(SchemaVersion, EdgeId, StreamId, Sequence, TagId, ContractRules.RawHash(Value), Quality, TimestampUtc);
}
