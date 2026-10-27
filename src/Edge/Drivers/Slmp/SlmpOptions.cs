using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Drivers.Slmp;

/// <summary>
/// Connection settings for one Mitsubishi FX5U over SLMP, bound from <c>Edge:Plc</c> (the section is shared with
/// S7; <c>Edge:Plc:Protocol</c> selects which options class is registered).
/// </summary>
public sealed class SlmpOptions
{
    public const string SectionName = "Edge:Plc";

    /// <summary>PLC IP address or host name. Required.</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// SLMP TCP port set in GX Works3 (Ethernet port, "External Device Configuration"). Required: the PLC has no
    /// fixed SLMP port, so there is no default.
    /// </summary>
    public int Port { get; set; }

    /// <summary>Budget for one request: lock wait + connect + request/response.</summary>
    public int TimeoutMs { get; set; } = 3000;
}

/// <summary>Rejects invalid <see cref="SlmpOptions"/> so the host fails at startup, not at the first PLC call.</summary>
internal sealed class SlmpOptionsValidator : IValidateOptions<SlmpOptions>
{
    private const int S7Port = 102;

    public ValidateOptionsResult Validate(string? name, SlmpOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            failures.Add($"{SlmpOptions.SectionName}:Host is required.");
        }

        if (options.Port == 0)
        {
            failures.Add($"{SlmpOptions.SectionName}:Port is required for Slmp; use the SLMP port set in GX Works3.");
        }
        else if (options.Port == S7Port)
        {
            failures.Add($"{SlmpOptions.SectionName}:Port {S7Port} is the S7 port; use the SLMP port set in GX Works3.");
        }
        else if (options.Port is < 1 or > 65535)
        {
            failures.Add($"{SlmpOptions.SectionName}:Port must be within 1..65535 (was {options.Port}).");
        }

        if (options.TimeoutMs is < 1000 or > 60000)
        {
            failures.Add($"{SlmpOptions.SectionName}:TimeoutMs must be within 1000..60000 (was {options.TimeoutMs}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
