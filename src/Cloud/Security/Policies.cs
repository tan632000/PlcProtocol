using Microsoft.AspNetCore.Authorization;

namespace IndustrialIot.Cloud.Security;

/// <summary>Authorization policy names and the rules behind them.</summary>
public static class Policies
{
    /// <summary>Read access: the Viewer or the Operator role.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Commands: the Operator role and a <c>sub</c> usable as the command's audit identity.</summary>
    public const string Operator = "Operator";

    internal const string SubjectClaim = "sub";

    internal static void Configure(AuthorizationOptions options, AuthOptions auth)
    {
        options.AddPolicy(Viewer, p => p.RequireAuthenticatedUser().RequireRole(auth.ViewerRole, auth.OperatorRole));
        options.AddPolicy(Operator, p => p
            .RequireAuthenticatedUser()
            .RequireRole(auth.OperatorRole)
            .RequireAssertion(ctx => IsValidSubject(ctx.User.FindFirst(SubjectClaim)?.Value)));

        // Anything without its own policy still needs a valid token; only health endpoints opt out.
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }

    /// <summary>
    /// The <c>DeviceCommand.IssuedBy</c> rule (Shared <c>ContractRules.PlainText</c>, max 128), restated because it
    /// is internal to Shared: an Operator whose <c>sub</c> could not be written into a command is refused up front.
    /// </summary>
    internal static bool IsValidSubject(string? sub)
    {
        if (string.IsNullOrWhiteSpace(sub) || sub.Length > 128)
        {
            return false;
        }

        for (var i = 0; i < sub.Length; i++)
        {
            var c = sub[i];
            if (char.IsControl(c))
            {
                return false;
            }

            if (char.IsHighSurrogate(c) && i + 1 < sub.Length && char.IsLowSurrogate(sub[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }
}
