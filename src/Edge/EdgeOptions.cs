using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge;

/// <summary>Identity of this Edge, bound from configuration section <c>Edge</c>.</summary>
public sealed class EdgeOptions
{
    public const string SectionName = "Edge";

    /// <summary>
    /// Unique id of this Edge; appears in every message and MQTT topic, so it uses the contract
    /// <c>edgeId</c> rule (no MQTT wildcard or separator characters can get in).
    /// </summary>
    public string EdgeId { get; set; } = "";
}

internal sealed partial class EdgeOptionsValidator : IValidateOptions<EdgeOptions>
{
    public ValidateOptionsResult Validate(string? name, EdgeOptions options) =>
        options.EdgeId is not null && EdgeIdPattern().IsMatch(options.EdgeId)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{EdgeOptions.SectionName}:EdgeId must be 1..64 chars [A-Za-z0-9_-].");

    // Same rule as the contract edgeId (src/Shared/Common/ContractJson.cs), which is internal to Shared.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EdgeIdPattern();
}
