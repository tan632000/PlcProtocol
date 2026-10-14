using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Drivers;

/// <summary>Connection settings for one S7-1200 PLC, bound from configuration section <c>Edge:Plc</c>.</summary>
public sealed class SiemensS7Options
{
    public const string SectionName = "Edge:Plc";

    /// <summary>PLC IP address or host name. Required.</summary>
    public string Host { get; set; } = "";

    /// <summary>ISO-on-TCP port; S7 always uses 102 unless a NAT/port-forward sits in between.</summary>
    public int Port { get; set; } = 102;

    /// <summary>Rack of the CPU; 0 for S7-1200.</summary>
    public int Rack { get; set; }

    /// <summary>Slot of the CPU; 1 for S7-1200.</summary>
    public int Slot { get; set; } = 1;

    /// <summary>Budget for one whole operation: lock wait + connect + request/response.</summary>
    public int TimeoutMs { get; set; } = 3000;
}

/// <summary>Rejects invalid <see cref="SiemensS7Options"/> so the host fails at startup, not at the first PLC call.</summary>
internal sealed class SiemensS7OptionsValidator : IValidateOptions<SiemensS7Options>
{
    public ValidateOptionsResult Validate(string? name, SiemensS7Options options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            failures.Add($"{SiemensS7Options.SectionName}:Host is required.");
        }

        AddIfOutOfRange(failures, nameof(options.Port), options.Port, 1, 65535);
        AddIfOutOfRange(failures, nameof(options.Rack), options.Rack, 0, 7);
        // S7netplus builds the remote TSAP from rack/slot and rejects slot > 15.
        AddIfOutOfRange(failures, nameof(options.Slot), options.Slot, 0, 15);
        AddIfOutOfRange(failures, nameof(options.TimeoutMs), options.TimeoutMs, 100, 60000);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void AddIfOutOfRange(List<string> failures, string field, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            failures.Add($"{SiemensS7Options.SectionName}:{field} must be within {min}..{max} (was {value}).");
        }
    }
}
