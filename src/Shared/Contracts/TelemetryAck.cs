using IndustrialIot.Shared.Common;

namespace IndustrialIot.Shared.Contracts;

/// <summary>
/// The Cloud's confirmation that every telemetry sample of one stream in the inclusive range
/// <see cref="FromSequence"/>..<see cref="ToSequence"/> is handled (stored, already stored, or permanently
/// rejected). Only then may the Edge delete those samples from its buffer.
/// </summary>
public sealed record TelemetryAck : IContract
{
    /// <summary>Widest range one ack may cover; bounds the delete one message can trigger.</summary>
    public const long MaxRangeWidth = 10_000;

    public required int SchemaVersion { get; init; }

    public required string EdgeId { get; init; }

    public required Guid StreamId { get; init; }

    public required long FromSequence { get; init; }

    public required long ToSequence { get; init; }

    public void Validate()
    {
        ContractRules.SchemaVersion(SchemaVersion);
        ContractRules.EdgeId(EdgeId);
        ContractRules.NonEmptyGuid(StreamId, "streamId");

        if (FromSequence < 0)
        {
            throw new ContractValidationException("fromSequence", "must be >= 0");
        }

        if (ToSequence < FromSequence)
        {
            throw new ContractValidationException("toSequence", "must be >= fromSequence");
        }

        if (ToSequence - FromSequence >= MaxRangeWidth)
        {
            throw new ContractValidationException("toSequence", $"range must cover fewer than {MaxRangeWidth} sequences");
        }
    }
}
