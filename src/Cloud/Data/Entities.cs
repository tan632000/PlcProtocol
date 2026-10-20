namespace IndustrialIot.Cloud.Data;

/// <summary>One stored telemetry sample. Primary key = the Edge's dedupe key (edge, stream, sequence).</summary>
public sealed class TelemetryRow
{
    public required string EdgeId { get; set; }

    public required Guid StreamId { get; set; }

    public required long Sequence { get; set; }

    public required string TagId { get; set; }

    /// <summary>JSON <c>true</c>/<c>false</c>/number, or null for bad samples.</summary>
    public string? Value { get; set; }

    public required string Quality { get; set; }

    /// <summary>Sample time from the Edge clock.</summary>
    public required DateTimeOffset TimestampUtc { get; set; }

    /// <summary>When the Cloud stored it (Cloud clock); retention uses this.</summary>
    public required DateTimeOffset ReceivedUtc { get; set; }
}

/// <summary>Newest known value per (edge, tag).</summary>
public sealed class LatestValueRow
{
    public required string EdgeId { get; set; }

    public required string TagId { get; set; }

    public string? Value { get; set; }

    public required string Quality { get; set; }

    public required DateTimeOffset TimestampUtc { get; set; }

    public required Guid StreamId { get; set; }

    public required long Sequence { get; set; }
}

/// <summary>A command issued through the API, with its delivery and Edge outcome.</summary>
public sealed class CommandRow
{
    public required Guid CommandId { get; set; }

    public required string EdgeId { get; set; }

    public required string TagId { get; set; }

    public required string TargetValue { get; set; }

    public required DateTimeOffset IssuedAtUtc { get; set; }

    public required DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>JWT <c>sub</c> of the operator (audit identity).</summary>
    public required string IssuedBy { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? RequestHash { get; set; }

    public required string Status { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public DateTimeOffset? ResultReceivedUtc { get; set; }

    public required DateTimeOffset CreatedUtc { get; set; }
}
