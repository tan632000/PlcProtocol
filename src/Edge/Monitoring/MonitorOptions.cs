using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Monitoring;

/// <summary>
/// Local, read-only monitor page, bound from configuration section <c>Edge:Monitor</c>. It always listens on
/// the loopback interface only: the page has no login, so it must never be reachable from the network.
/// </summary>
public sealed class MonitorOptions
{
    public const string SectionName = "Edge:Monitor";

    public bool Enabled { get; set; } = true;

    /// <summary>Opened as <c>http://localhost:{Port}/</c> on the Edge machine.</summary>
    public int Port { get; set; } = 5080;
}

internal sealed class MonitorOptionsValidator : IValidateOptions<MonitorOptions>
{
    public ValidateOptionsResult Validate(string? name, MonitorOptions options) =>
        options.Port is >= 1 and <= 65535
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{MonitorOptions.SectionName}:Port must be within 1..65535.");
}
