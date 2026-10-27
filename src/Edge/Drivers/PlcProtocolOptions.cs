using IndustrialIot.Edge.Commands;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Drivers;

public enum PlcProtocol
{
    S7,
    Slmp,
}

/// <summary>Selects the PLC protocol, bound from <c>Edge:Plc:Protocol</c>. S7 when absent.</summary>
public sealed class PlcProtocolOptions
{
    public const string SectionName = "Edge:Plc";

    public const string KeyName = "Edge:Plc:Protocol";

    public string Protocol { get; set; } = nameof(PlcProtocol.S7);

    /// <summary>Case-insensitive; a blank value counts as absent (S7).</summary>
    internal static bool TryParse(string? value, out PlcProtocol protocol)
    {
        protocol = PlcProtocol.S7;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // Match names only: Enum.TryParse would also accept numeric strings such as "1".
        var name = Enum.GetNames<PlcProtocol>().FirstOrDefault(n => n.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out protocol);
    }
}

/// <summary>Fails the start on an unknown protocol, or commands on a read-only one.</summary>
internal sealed class PlcProtocolOptionsValidator(IOptions<CommandsOptions> commands) : IValidateOptions<PlcProtocolOptions>
{
    public ValidateOptionsResult Validate(string? name, PlcProtocolOptions options)
    {
        if (!PlcProtocolOptions.TryParse(options.Protocol, out var protocol))
        {
            return ValidateOptionsResult.Fail(
                $"{PlcProtocolOptions.KeyName} must be one of S7, Slmp (was '{options.Protocol}').");
        }

        var failures = new List<string>();
        if (protocol != PlcProtocol.S7 && commands.Value.Enabled)
        {
            failures.Add(
                $"{CommandsOptions.SectionName}:Enabled cannot be true when {PlcProtocolOptions.KeyName} is {protocol}: only S7 supports writes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
