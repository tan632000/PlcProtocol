using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Polling;

/// <summary>Polling schedule, bound from configuration section <c>Edge:Poll</c>.</summary>
public sealed class PollOptions
{
    public const string SectionName = "Edge:Poll";

    /// <summary>Time between the starts of two poll cycles.</summary>
    public int IntervalMs { get; set; } = 1000;
}

internal sealed class PollOptionsValidator : IValidateOptions<PollOptions>
{
    public ValidateOptionsResult Validate(string? name, PollOptions options) =>
        options.IntervalMs is >= 100 and <= 3_600_000
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{PollOptions.SectionName}:IntervalMs must be within 100..3600000.");
}
