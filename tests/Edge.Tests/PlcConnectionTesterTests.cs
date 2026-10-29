using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// <c>POST /api/settings/test-connection</c> checks a proposed PLC connection without saving: a separate driver,
/// disposed after the test, or the running driver's own connection when the proposal names the PLC already in use.
/// Only the settings password holder, from the operator's page, one test at a time and at most 10 a minute.
/// </summary>
public sealed class PlcConnectionTesterTests : IAsyncDisposable
{
    private const string Password = "operator-password-1";
    private readonly string _contentRoot = Directory.CreateTempSubdirectory("plc-test-").FullName;
    private readonly int _port = ClosedPort();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly HttpClient _http;
    private IHost? _outer;

    public PlcConnectionTesterTests()
    {
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}"), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_outer is not null)
        {
            await _outer.StopAsync();
            _outer.Dispose();
        }

        EdgeTestHost.DeleteDirectory(_contentRoot);
    }

    [Fact]
    public async Task S7_AgainstAFakePlc_IsOk()
    {
        await using var plc = FakeS7Server.Start(FakeS7Mode.HandshakeThenRespond);
        await StartAsync();

        var (status, result) = await TestAsync(S7(plc.Port));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.True(result.GetProperty("elapsedMs").GetDouble() >= 0);
        await WaitUntilAsync(() => plc.OpenConnections == 0, "test connection closed");
    }

    [Fact]
    public async Task Slmp_AgainstAFakePlc_IsOk()
    {
        await using var plc = new FakeSlmpServer();
        await StartAsync();

        var (status, result) = await TestAsync(new JsonObject { ["Protocol"] = "Slmp", ["Host"] = "127.0.0.1", ["Port"] = plc.Port, ["TimeoutMs"] = 1000 });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.Equal(1, plc.Connections);
    }

    [Fact]
    public async Task ClosedPort_IsUnreachable_WithinTheTimeoutPlusOneSecond()
    {
        await StartAsync();
        var clock = Stopwatch.StartNew();

        var (status, result) = await TestAsync(S7(ClosedPort(), timeoutMs: 1000));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("unreachable", result.GetProperty("result").GetString());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
        Assert.Equal(2, result.EnumerateObject().Count());
    }

    [Fact]
    public async Task OneTestAtATime_409_AndAtMostTenAMinute_429()
    {
        await using var silent = FakeS7Server.Start(FakeS7Mode.BlackHole);
        await StartAsync();

        // The first test hangs on a PLC that never answers; a second one meanwhile is refused.
        var slow = TestAsync(S7(silent.Port, timeoutMs: 3000));
        await WaitUntilAsync(() => silent.AcceptCount == 1, "slow test connected");
        var (busy, _) = await TestAsync(S7(ClosedPort()));
        Assert.Equal(HttpStatusCode.Conflict, busy);
        Assert.Equal("unreachable", (await slow).Result.GetProperty("result").GetString());

        // 10 per minute in all (the slow one counted): 9 more pass, the 11th is refused until the minute is over.
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await TestAsync(S7(ClosedPort()))).Status);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await TestAsync(S7(ClosedPort()))).Status);
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await TestAsync(S7(ClosedPort()))).Status);
    }

    [Fact]
    public async Task WrongPassword_Is401_WithoutConnecting_AndCountsTowardTheSaveLockout()
    {
        await using var plc = FakeS7Server.Start(FakeS7Mode.HandshakeThenRespond);
        await StartAsync();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await TestAsync(S7(plc.Port), password: "wrong-" + i)).Status);
        }

        Assert.Equal(0, plc.AcceptCount);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await TestAsync(S7(plc.Port))).Status);

        // The save shares the lockout.
        var revision = JsonDocument.Parse(await _http.GetStringAsync("/api/settings")).RootElement.GetProperty("revision").GetString();
        var save = new JsonObject
        {
            ["revision"] = revision,
            ["password"] = Password,
            ["settings"] = new JsonObject { ["Plc"] = S7(plc.Port), ["Tags"] = new JsonArray() },
        };
        using var saved = await SendAsync("/api/settings", HttpMethod.Put, save.ToJsonString());
        Assert.Equal(HttpStatusCode.TooManyRequests, saved.StatusCode);
    }

    [Fact]
    public async Task ThePlcInUse_IsAnsweredByTheRunningDriver_WithoutASecondConnection()
    {
        await using var plc = FakeS7Server.Start(FakeS7Mode.HandshakeThenRespond, readData: [0, 7]);
        await StartAsync(plcPort: plc.Port, runAgentServices: true);
        await WaitUntilAsync(() => plc.OpenConnections == 1, "poller connected");
        var accepted = plc.AcceptCount;

        var (status, result) = await TestAsync(S7(plc.Port));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.Equal(accepted, plc.AcceptCount);
        Assert.Equal(1, plc.OpenConnections);
    }

    [Fact]
    public async Task CrossSiteOrigin_Is403_AndNothingConnects()
    {
        await using var plc = FakeS7Server.Start(FakeS7Mode.HandshakeThenRespond);
        await StartAsync();

        using var response = await SendAsync("/api/settings/test-connection", HttpMethod.Post, Body(S7(plc.Port), Password), origin: "http://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, plc.AcceptCount);
    }

    [Fact]
    public async Task InvalidPlc_Is400_WithKeyPaths()
    {
        await StartAsync();

        var (status, result) = await TestAsync(new JsonObject { ["Protocol"] = "S7", ["Host"] = "", ["Port"] = 70000, ["Extra"] = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Edge:Plc:Extra", result.GetRawText());
    }

    private async Task StartAsync(int? plcPort = null, bool runAgentServices = false)
    {
        SettingsPassword.Store(Path.Combine(_contentRoot, "data", "edge.settings-password.json"), Password);
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge:EdgeId"] = "plc-test",
            ["Edge:Plc:Host"] = "127.0.0.1",
            ["Edge:Plc:Port"] = (plcPort ?? ClosedPort()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Edge:Plc:TimeoutMs"] = "1000",
            ["Edge:Tags:0:TagId"] = "Line1.Speed",
            ["Edge:Tags:0:Address"] = "DB1.DBW2",
            ["Edge:Tags:0:DataType"] = "Int",
            ["Edge:Buffer:Path"] = "data/edge.db",
            ["Edge:Poll:IntervalMs"] = "200",
            ["Edge:Mqtt:Host"] = "127.0.0.1",
            ["Edge:Mqtt:Port"] = ClosedPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Edge:Mqtt:Username"] = "plc-test",
            ["Edge:Mqtt:Password"] = "test-only-password",
            ["Edge:Monitor:Enabled"] = "true",
            ["Edge:Monitor:Port"] = _port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _contentRoot });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddEdgeSupervisor(options => options.ConfigureInner = services =>
        {
            // Only the monitor (and the database it reads) unless the poller is wanted too.
            if (!runAgentServices)
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType != typeof(Buffering.EdgeDatabaseStarter)
                    && d.ImplementationType != typeof(Monitoring.MonitorServer)).ToList())
                {
                    services.Remove(descriptor);
                }
            }
        });
        _outer = builder.Build();
        await _outer.StartAsync();
    }

    private static JsonObject S7(int port, int timeoutMs = 1000, string host = "127.0.0.1") =>
        new() { ["Protocol"] = "S7", ["Host"] = host, ["Port"] = port, ["TimeoutMs"] = timeoutMs };

    private static string Body(JsonObject plc, string password) =>
        new JsonObject { ["password"] = password, ["plc"] = JsonNode.Parse(plc.ToJsonString()) }.ToJsonString();

    private async Task<(HttpStatusCode Status, JsonElement Result)> TestAsync(JsonObject plc, string password = Password)
    {
        using var response = await SendAsync("/api/settings/test-connection", HttpMethod.Post, Body(plc, password));
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, json.RootElement.Clone());
    }

    private async Task<HttpResponseMessage> SendAsync(string path, HttpMethod method, string body, string? origin = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        return await _http.SendAsync(request);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), $"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
