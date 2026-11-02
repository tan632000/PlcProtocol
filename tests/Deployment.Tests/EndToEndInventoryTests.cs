extern alias cloud;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using IndustrialIot.Edge;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Samples.InventoryClient;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using CloudMqttConnection = cloud::IndustrialIot.Cloud.Messaging.CloudMqttConnection;
using CloudProgram = cloud::Program;

namespace IndustrialIot.Deployment.Tests;

/// <summary>
/// The sample client (samples/InventoryClient) against the real path: an Edge with two simulated PLCs, Mosquitto with the
/// repository's configuration and ACL, the Cloud on a real socket and PostgreSQL. The sample sees only the URL, a Viewer
/// token and the Edge id, exactly what docs/scada-integration.md gives a SCADA project. Plus the doc and source checks.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EndToEndInventoryTests(InventoryStack stack) : IClassFixture<InventoryStack>
{
    [Fact]
    public async Task Sample_ListsThePlcsTagsAndARealtimeValue()
    {
        var output = new StringWriter();

        var exit = await InventoryClient.RunAsync(stack.CloudUri, stack.ViewerToken, InventoryStack.EdgeId, TimeSpan.FromSeconds(5), output, CancellationToken.None);

        var text = output.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("PLC Line (S7-1200): connected", text, StringComparison.Ordinal);
        Assert.Contains("PLC Press (FX5U): connected", text, StringComparison.Ordinal);
        foreach (var tagId in InventoryStack.TagIds)
        {
            Assert.Contains(tagId, text, StringComparison.Ordinal);
        }

        Assert.Contains("telemetry ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(stack.ViewerToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(stack.Queries, query => query.Contains("access_token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sample_WithAWrongToken_ExitsNonZero_PrintingTheStatusOnly()
    {
        var output = new StringWriter();
        var wrong = "not-a-token-" + Guid.NewGuid().ToString("N");

        var exit = await InventoryClient.RunAsync(stack.CloudUri, wrong, InventoryStack.EdgeId, TimeSpan.FromSeconds(1), output, CancellationToken.None);

        Assert.NotEqual(0, exit);
        Assert.Contains("401", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sample_WhenTheHubClosesOnTokenExpiry_SaysSoAndExitsNonZero()
    {
        var output = new StringWriter();
        var shortLived = stack.CreateToken(["Viewer"], TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();

        var exit = await InventoryClient.RunAsync(stack.CloudUri, shortLived, InventoryStack.EdgeId, TimeSpan.FromMinutes(5), output, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("hub closed", output.ToString(), StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"the closed hub was noticed only after {watch.Elapsed}");
    }

    [Fact]
    public async Task IntegrationDoc_ListsEveryRequestTheSampleMakes()
    {
        stack.Requests.Clear();
        await InventoryClient.RunAsync(stack.CloudUri, stack.ViewerToken, InventoryStack.EdgeId, TimeSpan.FromSeconds(1), new StringWriter(), CancellationToken.None);
        var routes = stack.Requests
            .Select(path => path
                .Replace($"/{InventoryStack.EdgeId}/", "/{edgeId}/", StringComparison.Ordinal)
                .Replace("/devices/Line/", "/devices/{device}/", StringComparison.Ordinal)
                .Replace("/devices/Press/", "/devices/{device}/", StringComparison.Ordinal))
            .Select(path => path.StartsWith("/hubs/live", StringComparison.Ordinal) ? "/hubs/live" : path)
            .Distinct()
            .ToList();

        var doc = File.ReadAllText(Path.Combine(Repo.Root, "docs", "scada-integration.md"));
        Assert.Contains("/api/v1/edges/{edgeId}/devices", routes);
        Assert.Contains("/api/v1/edges/{edgeId}/devices/{device}/tags", routes);
        Assert.Contains("/hubs/live", routes);
        Assert.All(routes, route => Assert.Contains(route, doc, StringComparison.Ordinal));
    }

    [Fact]
    public void SampleSource_SendsTheTokenOnlyAsAHeader()
    {
        var source = File.ReadAllText(Path.Combine(Repo.Root, "samples", "InventoryClient", "Program.cs"));

        Assert.DoesNotContain("access_token", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AccessTokenProvider", source, StringComparison.Ordinal);
        Assert.Contains("new AuthenticationHeaderValue(\"Bearer\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeDocs_StateTheBrokerFirst()
    {
        var deployment = File.ReadAllText(Path.Combine(Repo.Root, "docs", "deployment.md"));
        var server = File.ReadAllText(Path.Combine(Repo.Root, "docs", "server-deploy.md"));

        Assert.Contains("Broker refused subscription(s): iiot/+/inventory", deployment, StringComparison.Ordinal);
        Assert.Contains("broker → Cloud → Edge", deployment, StringComparison.Ordinal);
        Assert.Contains("iiot/+/inventory", server, StringComparison.Ordinal);
        Assert.Contains("trước khi CD deploy", server, StringComparison.Ordinal);
    }
}

/// <summary>Secrets, PostgreSQL, the repository's broker, the Cloud on Kestrel and a two-PLC Edge, shared by the class.</summary>
public sealed class InventoryStack : IAsyncLifetime
{
    public const string EdgeId = "line1-edge01";

    public static readonly string[] TagIds = ["Line.Speed", "Line.Count", "Press.D100", "Press.D101"];

    private const string Issuer = "https://idp.test/";
    private const string Audience = "api://iiot-cloud";

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "e2e-key" };
    private readonly string _edgeRoot = Path.Combine(Path.GetTempPath(), "iiot-e2e-inventory-" + Guid.NewGuid().ToString("N"));
    private SecretsDirectory? _secrets;
    private PostgreSqlContainer? _database;
    private BrokerFixture? _broker;
    private CloudOnKestrel? _cloud;
    private IHost? _edge;

    // Runs BrokerFixture's static constructor (pins the Testcontainers resource reaper image) before any container starts.
    static InventoryStack() => RuntimeHelpers.RunClassConstructor(typeof(BrokerFixture).TypeHandle);

    public Uri CloudUri { get; private set; } = null!;

    public string ViewerToken { get; private set; } = "";

    /// <summary>Paths of the requests the Cloud served (no query strings).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Query strings of the requests the Cloud served, to prove no token travels in a URL.</summary>
    public ConcurrentQueue<string> Queries { get; } = new();

    public async Task InitializeAsync()
    {
        _secrets = SecretsDirectory.Create("--edge", EdgeId);
        _database = new PostgreSqlBuilder("postgres@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24").Build();
        await _database.StartAsync();
        _broker = await BrokerFixture.StartAsync(_secrets);

        var port = FreePort();
        _cloud = new CloudOnKestrel(_broker.Port, _secrets, _database.GetConnectionString(), _signingKey, Requests, Queries);
        _cloud.UseKestrel(port);
        _cloud.StartServer();
        CloudUri = new Uri($"http://127.0.0.1:{port}/");
        ViewerToken = CreateToken(["Viewer"]);
        var connection = _cloud.Services.GetRequiredService<CloudMqttConnection>();
        await WaitUntilAsync(() => Task.FromResult(connection.IsConnected), "Cloud connected");

        await StartEdgeAsync();
        await WaitUntilAsync(InventoryStoredAsync, "the Edge's inventory stored within 30 s");
    }

    public async Task DisposeAsync()
    {
        // Every step runs even when an earlier one fails, so no container, secret or temp folder is left behind.
        await QuietlyAsync(async () =>
        {
            if (_edge is not null)
            {
                await _edge.StopAsync();
                _edge.Dispose();
            }
        });
        await QuietlyAsync(async () =>
        {
            if (_cloud is not null)
            {
                await _cloud.DisposeAsync();
            }
        });
        await QuietlyAsync(async () =>
        {
            if (_broker is not null)
            {
                await _broker.DisposeAsync();
            }
        });
        await QuietlyAsync(async () =>
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        });
        _secrets?.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_edgeRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file may still be closing; the OS temp cleanup will take it.
        }
    }

    private static async Task QuietlyAsync(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cleanup of a test stack: the next step still runs.
        }
    }

    private async Task StartEdgeAsync()
    {
        Directory.CreateDirectory(_edgeRoot);
        var settings = new Dictionary<string, string?>
        {
            ["Edge:EdgeId"] = EdgeId,
            ["Edge:Buffer:Path"] = Path.Combine(_edgeRoot, "edge.db"),
            ["Edge:Mqtt:Host"] = "127.0.0.1",
            ["Edge:Mqtt:Port"] = _broker!.Port.ToString(CultureInfo.InvariantCulture),
            ["Edge:Mqtt:Username"] = EdgeId,
            ["Edge:Mqtt:Password"] = File.ReadAllText(_secrets!.File($"passwords/{EdgeId}.txt")),
            ["Edge:Mqtt:CaCertificatePath"] = _secrets.File("ca.pem"),
            ["Edge:Mqtt:ReconnectMinDelayMs"] = "200",
            ["Edge:Mqtt:ReconnectMaxDelayMs"] = "1000",
            ["Edge:Monitor:Enabled"] = "false",
            ["Edge:Channels:0:Name"] = "S7",
            ["Edge:Channels:0:Driver"] = "S7",
            ["Edge:Channels:0:Devices:0:Name"] = "Line",
            ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
            ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1", // unused: the simulated PLC replaces the driver
            ["Edge:Channels:0:Devices:0:Tags:0:Name"] = "Speed",
            ["Edge:Channels:0:Devices:0:Tags:0:Address"] = "DB1.DBW0",
            ["Edge:Channels:0:Devices:0:Tags:0:DataType"] = "Int",
            ["Edge:Channels:0:Devices:0:Tags:0:Description"] = "Belt speed",
            ["Edge:Channels:0:Devices:0:Tags:1:Name"] = "Count",
            ["Edge:Channels:0:Devices:0:Tags:1:Address"] = "DB1.DBW2",
            ["Edge:Channels:0:Devices:0:Tags:1:DataType"] = "Int",
            ["Edge:Channels:1:Name"] = "Fx",
            ["Edge:Channels:1:Driver"] = "Slmp3E",
            ["Edge:Channels:1:Devices:0:Name"] = "Press",
            ["Edge:Channels:1:Devices:0:Model"] = "FX5U",
            ["Edge:Channels:1:Devices:0:Host"] = "127.0.0.2",
            ["Edge:Channels:1:Devices:0:Port"] = "5000",
            ["Edge:Channels:1:Devices:0:Tags:0:Name"] = "D100",
            ["Edge:Channels:1:Devices:0:Tags:0:Address"] = "D100",
            ["Edge:Channels:1:Devices:0:Tags:0:DataType"] = "Int",
            ["Edge:Channels:1:Devices:0:Tags:1:Name"] = "D101",
            ["Edge:Channels:1:Devices:0:Tags:1:Address"] = "D101",
            ["Edge:Channels:1:Devices:0:Tags:1:DataType"] = "Int",
        };

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _edgeRoot });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddEdgeAgent(builder.Configuration);
        builder.Services.AddSingleton<IPlcDriverFactory>(new SimulatedPlcFactory());
        _edge = builder.Build();
        await _edge.StartAsync();
    }

    private async Task<bool> InventoryStoredAsync()
    {
        await using var connection = new NpgsqlConnection(_database!.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM edge_inventory WHERE edge_id = @edge";
        command.Parameters.AddWithValue("edge", EdgeId);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    /// <summary>A Viewer-style access token signed with the stack's key; <paramref name="lifetime"/> defaults to an hour.</summary>
    public string CreateToken(string[] roles, TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            TokenType = "at+jwt",
            IssuedAt = now,
            NotBefore = now,
            Expires = now + (lifetime ?? TimeSpan.FromHours(1)),
            Claims = new Dictionary<string, object> { ["roles"] = roles, ["sub"] = "e2e-viewer" },
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), $"timed out waiting for: {what}");
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// The real Cloud host on a real socket. Tokens are validated against a key held in memory (no IdP to reach), and
    /// every request path is recorded for the integration doc check.
    /// </summary>
    private sealed class CloudOnKestrel(
        int brokerPort,
        SecretsDirectory secrets,
        string connectionString,
        SecurityKey signingKey,
        ConcurrentQueue<string> requests,
        ConcurrentQueue<string> queries)
        : WebApplicationFactory<CloudProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var settings = new Dictionary<string, string>
            {
                ["Cloud:Database:ConnectionString"] = connectionString,
                ["Cloud:Edges:0:EdgeId"] = EdgeId,
                ["Cloud:Edges:0:DisplayName"] = "Line 1",
                ["Cloud:Auth:Authority"] = Issuer,
                ["Cloud:Auth:Audience"] = Audience,
                ["Cloud:Mqtt:Host"] = "127.0.0.1",
                ["Cloud:Mqtt:Port"] = brokerPort.ToString(CultureInfo.InvariantCulture),
                ["Cloud:Mqtt:Username"] = "iiot-cloud",
                ["Cloud:Mqtt:Password"] = File.ReadAllText(secrets.File("passwords/iiot-cloud.txt")),
                ["Cloud:Mqtt:CaCertificatePath"] = secrets.File("ca.pem"),
            };
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                metadata.SigningKeys.Add(signingKey);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
                    jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
                services.AddSingleton<IStartupFilter>(new RecordRequests(requests, queries));
            });
        }
    }

    private sealed class RecordRequests(ConcurrentQueue<string> requests, ConcurrentQueue<string> queries) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
            {
                requests.Enqueue(context.Request.Path.Value ?? "");
                queries.Enqueue(context.Request.QueryString.Value ?? "");
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
