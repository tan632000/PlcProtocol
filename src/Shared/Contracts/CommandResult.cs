using IndustrialIot.Shared.Common;

namespace IndustrialIot.Shared.Contracts;

/// <summary>The Edge's answer to a <see cref="DeviceCommand"/>, correlated by <see cref="CommandId"/>.</summary>
public sealed record CommandResult : IContract
{
    public required int SchemaVersion { get; init; }

    public required Guid CommandId { get; init; }

    public required string EdgeId { get; init; }

    public required CommandOutcome Outcome { get; init; }

    /// <summary>
    /// Optional snake_case code. Producers use <see cref="CommandReasons"/>; receivers must tolerate
    /// codes they do not know yet (a newer Edge may add one).
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Stored at millisecond precision (the wire precision).</summary>
    public required DateTimeOffset CompletedAtUtc
    {
        get;
        init => field = ContractRules.TruncateToMilliseconds(value);
    }

    public void Validate()
    {
        ContractRules.SchemaVersion(SchemaVersion);
        ContractRules.NonEmptyGuid(CommandId, "commandId");
        ContractRules.EdgeId(EdgeId);
        ContractRules.Defined(Outcome, "outcome");
        ContractRules.Reason(Reason);
        ContractRules.Utc(CompletedAtUtc, "completedAtUtc");
    }
}
