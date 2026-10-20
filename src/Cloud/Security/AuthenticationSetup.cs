using System.Net;
using IndustrialIot.Cloud.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IndustrialIot.Cloud.Security;

/// <summary>JWT bearer validation, role policies and trusted-proxy handling.</summary>
public static class AuthenticationSetup
{
    private static readonly PathString LiveHubPath = new(Live.LiveHub.Path);

    public static IServiceCollection AddCloudSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>());

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) => ConfigureJwt(jwt, auth.Value));

        services.AddAuthorization();
        services.AddOptions<AuthorizationOptions>()
            .Configure<IOptions<AuthOptions>>((authorization, auth) => Policies.Configure(authorization, auth.Value));

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersSettings>>((forwarded, settings) => ConfigureForwardedHeaders(forwarded, settings.Value));

        return services;
    }

    private static void ConfigureJwt(JwtBearerOptions jwt, AuthOptions auth)
    {
        jwt.Authority = auth.Authority;
        jwt.Audience = auth.Audience;
        jwt.RequireHttpsMetadata = true;

        // Keep claim names as issued ("sub", "roles"), not remapped to long WS-* URIs.
        jwt.MapInboundClaims = false;

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            // Issuer and signing keys come from the authority's metadata.
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidAudience = auth.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(auth.ClockSkewSeconds),

            // Asymmetric only: an HS256 token "signed" with a public key must never validate.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSsaPssSha256],

            // Access tokens only: an ID token (typ id+jwt) proves who logged in, not what the API may do.
            ValidTypes = auth.EffectiveValidTypes,
            NameClaimType = Policies.SubjectClaim,
            RoleClaimType = auth.RoleClaimType,
        };

        jwt.Events = new JwtBearerEvents
        {
            // Browsers cannot set headers on a WebSocket, so the hub alone may take the token from the query.
            // Everywhere else a query token is ignored: URLs end up in logs, proxies and browser history.
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments(LiveHubPath)
                    && context.Request.Query.TryGetValue("access_token", out var token)
                    && token.Count == 1)
                {
                    context.Token = token[0];
                }

                return Task.CompletedTask;
            },
            // Replace the default challenge: its error_description could echo validation details.
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await ProblemMapping.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "unauthorized").ConfigureAwait(false);
            },
            OnForbidden = context =>
                ProblemMapping.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, "forbidden"),
        };
    }

    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions forwarded, ForwardedHeadersSettings settings)
    {
        // Fail closed: with both lists empty the middleware skips the address check and trusts every client.
        forwarded.ForwardedHeaders = settings.KnownProxies.Count == 0
            ? ForwardedHeaders.None
            : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwarded.ForwardLimit = 1;

        // The defaults trust loopback; behind a proxy on the same host that would let any local process spoof headers.
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
        foreach (var proxy in settings.KnownProxies)
        {
            forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
        }
    }
}
