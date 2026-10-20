using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Messaging;

/// <summary>
/// Broker connection, bound from <c>Cloud:Mqtt</c>. TLS is always on. A plain class on purpose: a record's
/// generated <c>ToString</c> would print the password.
/// </summary>
public sealed class CloudMqttOptions
{
    public const string SectionName = "Cloud:Mqtt";

    public string Host { get; set; } = "";

    public int Port { get; set; } = 8883;

    public string Username { get; set; } = "";

    /// <summary>Supply through <c>Cloud__Mqtt__Password</c>, never in appsettings.</summary>
    public string Password { get; set; } = "";

    /// <summary>Optional PEM file of a private CA; when set, only that CA is trusted.</summary>
    public string? CaCertificatePath { get; set; }

    public string ClientId { get; set; } = "iiot-cloud";

    public int KeepAliveSeconds { get; set; } = 30;

    public int ReconnectMinDelayMs { get; set; } = 1000;

    public int ReconnectMaxDelayMs { get; set; } = 30_000;
}

/// <summary>Messages name the key only — never the value, which may be the password.</summary>
internal sealed partial class CloudMqttOptionsValidator : IValidateOptions<CloudMqttOptions>
{
    public ValidateOptionsResult Validate(string? name, CloudMqttOptions o)
    {
        var failures = new List<string>();
        const string s = CloudMqttOptions.SectionName;

        if (string.IsNullOrWhiteSpace(o.Host))
        {
            failures.Add($"{s}:Host is required.");
        }

        if (o.Port is < 1 or > 65535)
        {
            failures.Add($"{s}:Port must be within 1..65535.");
        }

        if (string.IsNullOrEmpty(o.Username) || o.Username.Length > 256)
        {
            failures.Add($"{s}:Username is required and must be at most 256 characters.");
        }

        if (string.IsNullOrEmpty(o.Password) || o.Password.Length > 1024)
        {
            failures.Add($"{s}:Password is required (set Cloud__Mqtt__Password) and must be at most 1024 characters.");
        }

        if (o.CaCertificatePath is { Length: > 0 } ca && !File.Exists(ca))
        {
            failures.Add($"{s}:CaCertificatePath points to a file that does not exist.");
        }

        if (o.ClientId is null || !ClientIdPattern().IsMatch(o.ClientId))
        {
            failures.Add($"{s}:ClientId must be 1..64 chars [A-Za-z0-9_-].");
        }

        if (o.KeepAliveSeconds is < 5 or > 1200)
        {
            failures.Add($"{s}:KeepAliveSeconds must be within 5..1200.");
        }

        if (o.ReconnectMinDelayMs is < 100 or > 60_000)
        {
            failures.Add($"{s}:ReconnectMinDelayMs must be within 100..60000.");
        }

        if (o.ReconnectMaxDelayMs < o.ReconnectMinDelayMs || o.ReconnectMaxDelayMs > 600_000)
        {
            failures.Add($"{s}:ReconnectMaxDelayMs must be at least ReconnectMinDelayMs and at most 600000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClientIdPattern();
}
