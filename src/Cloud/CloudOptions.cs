using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud;

/// <summary>PostgreSQL connection, bound from <c>Cloud:Database</c>. The connection string comes from the environment.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Cloud:Database";

    /// <summary>Set through <c>Cloud__Database__ConnectionString</c>; never logged or returned.</summary>
    public string ConnectionString { get; set; } = "";
}

/// <summary>One Edge the Cloud accepts data from and sends commands to.</summary>
public sealed class EdgeRegistration
{
    public string EdgeId { get; set; } = "";

    public string? DisplayName { get; set; }
}

/// <summary>The allow-list of Edges, bound from <c>Cloud</c> so the array <c>Cloud:Edges</c> lands in <see cref="Edges"/>.</summary>
public sealed class EdgesOptions
{
    public List<EdgeRegistration> Edges { get; set; } = [];

    public bool IsAllowed(string edgeId) => Edges.Any(e => string.Equals(e.EdgeId, edgeId, StringComparison.Ordinal));
}

/// <summary>Remote writes and their lifetimes, bound from <c>Cloud:Commands</c>.</summary>
public sealed class CommandOptions
{
    public const string SectionName = "Cloud:Commands";

    /// <summary>
    /// Off by default: the Cloud (and any SCADA built on it) only displays data. Off means no command route is mapped
    /// (404), no command result is consumed and no timeout sweep runs. A write also needs the Edge switch, the broker's
    /// command ACL and a writable tag. Read at start; changing it needs a restart.
    /// </summary>
    public bool Enabled { get; set; }

    public int DefaultTtlSeconds { get; set; } = 60;

    public int MaxTtlSeconds { get; set; } = 600;

    public int TimeoutGraceSeconds { get; set; } = 120;
}

/// <summary>Data retention, bound from <c>Cloud:Retention</c>.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Cloud:Retention";

    public int TelemetryDays { get; set; } = 90;
}

/// <summary>Readiness refresh, bound from <c>Cloud:Health</c>.</summary>
public sealed class HealthOptions
{
    public const string SectionName = "Cloud:Health";

    public int RefreshSeconds { get; set; } = 15;
}

/// <summary>Reverse proxies whose <c>X-Forwarded-*</c> headers are trusted, bound from <c>Cloud:ForwardedHeaders</c>.</summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "Cloud:ForwardedHeaders";

    public List<string> KnownProxies { get; set; } = [];
}

/// <summary>Validates every Cloud section at start. Messages name the key, never its value.</summary>
internal sealed partial class CloudOptionsValidator :
    IValidateOptions<DatabaseOptions>,
    IValidateOptions<EdgesOptions>,
    IValidateOptions<CommandOptions>,
    IValidateOptions<RetentionOptions>,
    IValidateOptions<HealthOptions>,
    IValidateOptions<ForwardedHeadersSettings>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail($"{DatabaseOptions.SectionName}:ConnectionString is required (set Cloud__Database__ConnectionString).")
            : ValidateOptionsResult.Success;

    public ValidateOptionsResult Validate(string? name, EdgesOptions options)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.Edges.Count; i++)
        {
            var edgeId = options.Edges[i]?.EdgeId;
            if (edgeId is null || !EdgeIdPattern().IsMatch(edgeId))
            {
                failures.Add($"Cloud:Edges:{i}:EdgeId must be 1..64 chars [A-Za-z0-9_-].");
            }
            else if (!seen.Add(edgeId))
            {
                failures.Add($"Cloud:Edges:{i}:EdgeId is a duplicate.");
            }
        }

        return Result(failures);
    }

    public ValidateOptionsResult Validate(string? name, CommandOptions options)
    {
        var failures = new List<string>();
        const string s = CommandOptions.SectionName;
        if (options.MaxTtlSeconds is < 5 or > 86_400)
        {
            failures.Add($"{s}:MaxTtlSeconds must be within 5..86400.");
        }

        if (options.DefaultTtlSeconds < 5 || options.DefaultTtlSeconds > options.MaxTtlSeconds)
        {
            failures.Add($"{s}:DefaultTtlSeconds must be within 5..MaxTtlSeconds.");
        }

        if (options.TimeoutGraceSeconds is < 0 or > 3_600)
        {
            failures.Add($"{s}:TimeoutGraceSeconds must be within 0..3600.");
        }

        return Result(failures);
    }

    public ValidateOptionsResult Validate(string? name, RetentionOptions options) =>
        options.TelemetryDays is < 1 or > 3_650
            ? ValidateOptionsResult.Fail($"{RetentionOptions.SectionName}:TelemetryDays must be within 1..3650.")
            : ValidateOptionsResult.Success;

    public ValidateOptionsResult Validate(string? name, HealthOptions options) =>
        options.RefreshSeconds is < 1 or > 300
            ? ValidateOptionsResult.Fail($"{HealthOptions.SectionName}:RefreshSeconds must be within 1..300.")
            : ValidateOptionsResult.Success;

    public ValidateOptionsResult Validate(string? name, ForwardedHeadersSettings options)
    {
        var failures = new List<string>();
        for (var i = 0; i < options.KnownProxies.Count; i++)
        {
            if (!IsFullIpAddress(options.KnownProxies[i]))
            {
                failures.Add($"{ForwardedHeadersSettings.SectionName}:KnownProxies:{i} must be an IP address.");
            }
        }

        return Result(failures);
    }

    // IPAddress.TryParse also accepts shorthand such as "10.1" (10.0.0.1); a typo must not trust a proxy.
    private static bool IsFullIpAddress(string? value) =>
        value is not null
        && IPAddress.TryParse(value, out var address)
        && (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 || value.Split('.').Length == 4);

    private static ValidateOptionsResult Result(List<string> failures) =>
        failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

    // Same rule as the contract edgeId (src/Shared/Common/ContractJson.cs).
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EdgeIdPattern();
}
