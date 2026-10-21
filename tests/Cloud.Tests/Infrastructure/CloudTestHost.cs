using System.Security.Cryptography;
using IndustrialIot.Cloud.Security;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace IndustrialIot.Cloud.Tests.Infrastructure;

/// <summary>
/// The real Cloud host on a fresh database, connected to its own TLS test broker (<see cref="Broker"/>). Settings are applied with <c>UseSetting</c> before the host is
/// built, so options validation and the migrator see them (WebApplicationFactory configuration timing).
/// Each test states only what it changes; later tasks add defaults for their sections here.
/// </summary>
public sealed class CloudTestHost : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly bool _staticMetadata;

    /// <param name="overrides">Configuration keys to set; a <c>null</c> value removes the key.</param>
    /// <param name="configureServices">Runs after the app's registrations, e.g. to replace <c>TimeProvider</c>.</param>
    /// <param name="staticMetadata">
    /// False leaves JwtBearer's own configuration manager in place, so a test can serve the IdP metadata itself.
    /// </param>
    public CloudTestHost(
        PostgresFixture database,
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null,
        bool staticMetadata = true)
    {
        _staticMetadata = staticMetadata;
        // A broker per host: tests stop and restart it, and sessions must not leak between tests.
        Broker = TestMqttBroker.StartAsync("iiot-cloud", "test-only-password").GetAwaiter().GetResult();

        _settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cloud:Database:ConnectionString"] = database.NewDatabaseConnectionString(),
            ["Cloud:Edges:0:EdgeId"] = "edge-a",
            ["Cloud:Edges:0:DisplayName"] = "Edge A",
            ["Cloud:Edges:1:EdgeId"] = "edge-b",
            ["Cloud:Edges:1:DisplayName"] = "Edge B",
            ["Cloud:Auth:Authority"] = Issuer,
            ["Cloud:Auth:Audience"] = Audience,

            // On, so the command tests model write mode; the production default (off) is proven in CommandsDisabledTests.
            ["Cloud:Commands:Enabled"] = "true",
        };

        // The broker describes itself in Edge keys; the Cloud reads the same values from Cloud:Mqtt.
        foreach (var (key, value) in Broker.EdgeSettings())
        {
            _settings["Cloud:Mqtt:" + key["Edge:Mqtt:".Length..]] = value;
        }

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                _settings.Remove(key);
            }
            else
            {
                _settings[key] = value;
            }
        }

        _configureServices = configureServices;
    }

    public const string Issuer = "https://idp.test/";
    public const string Audience = "api://iiot-cloud";

    internal TestMqttBroker Broker { get; }

    /// <summary>The IdP's signing key; the host trusts it through static metadata instead of fetching any.</summary>
    public RsaSecurityKey SigningKey { get; } = new(RSA.Create(2048)) { KeyId = "test-key" };

    /// <summary>
    /// Also published in the metadata, so an HS256 token signed with it has a matching key: only the
    /// algorithm allow-list can reject it (a key published for HMAC is the classic algorithm-confusion setup).
    /// </summary>
    public SymmetricSecurityKey SymmetricKey { get; } = new(RandomNumberGenerator.GetBytes(32)) { KeyId = "hmac-key" };

    /// <summary>
    /// An access token as the IdP would issue it: RS256, <c>typ</c> <c>at+jwt</c>, valid for 10 minutes.
    /// <paramref name="adjust"/> changes any part, e.g. to make it invalid in one specific way.
    /// </summary>
    public string CreateToken(string[] roles, string? sub = "user-1", Action<SecurityTokenDescriptor>? adjust = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["roles"] = roles };
        if (sub is not null)
        {
            claims["sub"] = sub;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            TokenType = "at+jwt",
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        };
        adjust?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Counts database commands and can simulate an outage.</summary>
    public DatabaseFaultInterceptor DatabaseFaults { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IInterceptor>(DatabaseFaults);

            // Metadata the IdP would publish, held in memory. Set as the ConfigurationManager, which the
            // JwtBearer post-configure only creates when none is set, so registration order does not matter.
            if (_staticMetadata)
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                metadata.SigningKeys.Add(SigningKey);
                metadata.SigningKeys.Add(SymmetricKey);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
                    jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
            }

            // Test-only routes, mapped by the production hook after the auth middleware like any module.
            services.AddSingleton<IEndpointModule, TestEndpoints>();
            _configureServices?.Invoke(services);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Broker.DisposeAsync();
    }

    private sealed class TestEndpoints : IEndpointModule
    {
        public void Map(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/test/operator", () => "operator").RequireAuthorization(Policies.Operator);
            endpoints.MapGet("/test/open", () => "open"); // no policy of its own: the fallback applies
            endpoints.MapGet("/test/scheme", (HttpContext http) => http.Request.Scheme).AllowAnonymous();
            endpoints.MapGet("/test/boom", string () => throw new InvalidOperationException("secret-detail")).AllowAnonymous();
        }
    }
}
