using System.Text.Json;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Shared.Contracts;

/// <summary>A request from the Cloud to write one PLC tag on a specific Edge.</summary>
public sealed record DeviceCommand : IContract
{
    /// <summary>Largest tolerated difference between Cloud and Edge clocks.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Longest allowed lifetime (<c>expiresAtUtc − issuedAtUtc</c>).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    public required int SchemaVersion { get; init; }

    /// <summary>Idempotency key: the Edge executes a given id at most once.</summary>
    public required Guid CommandId { get; init; }

    public required string EdgeId { get; init; }

    public required string TagId { get; init; }

    /// <summary>JSON <c>true</c>/<c>false</c>/number; the Edge validates it against the tag's data type.</summary>
    public required JsonElement? TargetValue
    {
        get;
        init => field = ContractRules.NormalizeValue(value);
    }

    /// <summary>Stored at millisecond precision (the wire precision).</summary>
    public required DateTimeOffset IssuedAtUtc
    {
        get;
        init => field = ContractRules.TruncateToMilliseconds(value);
    }

    /// <summary>Stored at millisecond precision (the wire precision).</summary>
    public required DateTimeOffset ExpiresAtUtc
    {
        get;
        init => field = ContractRules.TruncateToMilliseconds(value);
    }

    /// <summary>Cloud user or service id that issued the command (never a secret).</summary>
    public required string IssuedBy { get; init; }

    /// <summary>
    /// Decides whether the command may run now, using the Edge clock.
    /// Clock skew is checked first so a wrong clock is reported as such, not as "expired".
    /// </summary>
    public CommandFreshness CheckFreshness(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();

        if (IssuedAtUtc > now + MaxClockSkew)
        {
            return CommandFreshness.ClockSkew;
        }

        // Inclusive: at exactly expiresAtUtc the command is already expired.
        return now >= ExpiresAtUtc ? CommandFreshness.Expired : CommandFreshness.Fresh;
    }

    public void Validate()
    {
        ContractRules.SchemaVersion(SchemaVersion);
        ContractRules.NonEmptyGuid(CommandId, "commandId");
        ContractRules.EdgeId(EdgeId);
        ContractRules.TagId(TagId);

        if (TargetValue is not { } targetValue)
        {
            throw new ContractValidationException("targetValue", "must not be null");
        }

        ContractRules.ScalarValue(targetValue, "targetValue");
        ContractRules.Utc(IssuedAtUtc, "issuedAtUtc");
        ContractRules.Utc(ExpiresAtUtc, "expiresAtUtc");

        if (ExpiresAtUtc <= IssuedAtUtc)
        {
            throw new ContractValidationException("expiresAtUtc", "must be later than issuedAtUtc");
        }

        if (ExpiresAtUtc - IssuedAtUtc > MaxLifetime)
        {
            throw new ContractValidationException("expiresAtUtc", $"must be at most {MaxLifetime.TotalHours} h after issuedAtUtc");
        }

        ContractRules.PlainText(IssuedBy, "issuedBy", maxLength: 128);
    }

    public bool Equals(DeviceCommand? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && CommandId == other.CommandId
        && EdgeId == other.EdgeId
        && TagId == other.TagId
        && ContractRules.RawEquals(TargetValue, other.TargetValue)
        && IssuedAtUtc == other.IssuedAtUtc
        && ExpiresAtUtc == other.ExpiresAtUtc
        && IssuedBy == other.IssuedBy;

    public override int GetHashCode() =>
        HashCode.Combine(SchemaVersion, CommandId, EdgeId, TagId, ContractRules.RawHash(TargetValue), IssuedAtUtc, ExpiresAtUtc, IssuedBy);
}
