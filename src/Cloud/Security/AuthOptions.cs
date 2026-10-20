using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Security;

/// <summary>JWT validation against an external identity provider, bound from <c>Cloud:Auth</c>.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Cloud:Auth";

    public static readonly IReadOnlyList<string> DefaultValidTypes = ["at+jwt", "JWT"];

    /// <summary>The IdP's issuer URL (absolute <c>https://</c>); its OIDC metadata provides issuer and signing keys.</summary>
    public string Authority { get; set; } = "";

    /// <summary>This API's resource identifier as the IdP puts it in <c>aud</c> — never a client id.</summary>
    public string Audience { get; set; } = "";

    public string RoleClaimType { get; set; } = "roles";

    public string ViewerRole { get; set; } = "Viewer";

    public string OperatorRole { get; set; } = "Operator";

    /// <summary>
    /// Accepted <c>typ</c> header values. Null by default on purpose: the configuration binder appends to a
    /// pre-filled list, so configured values would otherwise be added to the defaults instead of replacing them.
    /// </summary>
    public List<string>? ValidTypes { get; set; }

    public int ClockSkewSeconds { get; set; } = 30;

    public IReadOnlyList<string> EffectiveValidTypes => ValidTypes is { Count: > 0 } configured ? configured : DefaultValidTypes;
}

/// <summary>Messages name the key only, never the value.</summary>
internal sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions o)
    {
        var failures = new List<string>();
        const string s = AuthOptions.SectionName;

        // https only: the signing keys are fetched from here, so a plain-HTTP authority lets anyone on the path mint tokens.
        if (!Uri.TryCreate(o.Authority, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{s}:Authority is required and must be an absolute https:// URL.");
        }

        if (string.IsNullOrWhiteSpace(o.Audience))
        {
            failures.Add($"{s}:Audience is required (the API resource identifier).");
        }

        if (string.IsNullOrWhiteSpace(o.RoleClaimType))
        {
            failures.Add($"{s}:RoleClaimType is required.");
        }

        if (string.IsNullOrWhiteSpace(o.ViewerRole) || string.IsNullOrWhiteSpace(o.OperatorRole))
        {
            failures.Add($"{s}:ViewerRole and {s}:OperatorRole are required.");
        }

        if (o.ValidTypes is { } types && types.Exists(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{s}:ValidTypes must not contain empty entries.");
        }

        if (o.ClockSkewSeconds is < 0 or > 300)
        {
            failures.Add($"{s}:ClockSkewSeconds must be within 0..300.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
