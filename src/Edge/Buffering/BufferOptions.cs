using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Buffering;

/// <summary>Local store-and-forward buffer, bound from configuration section <c>Edge:Buffer</c>.</summary>
public sealed class BufferOptions
{
    public const string SectionName = "Edge:Buffer";

    /// <summary>SQLite file; a relative path is resolved against the host content root.</summary>
    public string Path { get; set; } = "data/edge.db";

    /// <summary>Samples older than this are dropped (default 7 days).</summary>
    public int MaxAgeHours { get; set; } = 168;

    /// <summary>Above this many samples the oldest are dropped.</summary>
    public int MaxRows { get; set; } = 1_000_000;
}

internal sealed class BufferOptionsValidator : IValidateOptions<BufferOptions>
{
    public ValidateOptionsResult Validate(string? name, BufferOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Path))
        {
            failures.Add($"{BufferOptions.SectionName}:Path is required.");
        }

        if (options.MaxAgeHours is < 1 or > 8760)
        {
            failures.Add($"{BufferOptions.SectionName}:MaxAgeHours must be within 1..8760.");
        }

        if (options.MaxRows is < 1_000 or > 100_000_000)
        {
            failures.Add($"{BufferOptions.SectionName}:MaxRows must be within 1000..100000000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
