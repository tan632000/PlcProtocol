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
    public async Task SeveralPlcs_Is409_AndNothingConnects()
    {
        var driver = new FakePlcDriver();
        await using var edge = EdgeTestHost.Create(
            EdgeTestHost.MultiDevice(new Dictionary<string, string?>
            {
                ["Edge:Channels:0:Name"] = "S7",
                ["Edge:Channels:0:Driver"] = "S7",
                ["Edge:Channels:0:Devices:0:Name"] = "A",
                ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
                ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1",
                ["Edge:Channels:0:Devices:1:Name"] = "B",
                ["Edge:Channels:0:Devices:1:Model"] = "S7-1200",
                ["Edge:Channels:0:Devices:1:Host"] = "127.0.0.2",
            }),
            services => services.UseDriver(driver));
        var tester = edge.Services.GetRequiredService<PlcConnectionTester>();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body(S7(ClosedPort()), Password)));
        context.Response.Body = new MemoryStream();

        await tester.TestAsync(context, _port);

        Assert.Equal((int)HttpStatusCode.Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Equal("several-plcs", JsonDocument.Parse(context.Response.Body).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TheOnlyDeviceOfAMultiDeviceEdge_IsAnsweredByItsRunningDriver()
    {
        var driver = new ReachabilityDriver();
        var plcPort = ClosedPort();
        await using var edge = EdgeTestHost.Create(
            EdgeTestHost.MultiDevice(new Dictionary<string, string?>
            {
                ["Edge:Channels:0:Name"] = "S7",
                ["Edge:Channels:0:Driver"] = "S7",
                ["Edge:Channels:0:Devices:0:Name"] = "A",
                ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
                ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1",
                ["Edge:Channels:0:Devices:0:Port"] = plcPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }),
            services => services.UseDriver(driver));
        SettingsPassword.Store(SettingsPassword.PathFor(edge.Services.GetRequiredService<EdgeSettingsFile>().Path), Password);
        var tester = edge.Services.GetRequiredService<PlcConnectionTester>();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body(S7(plcPort), Password)));
        context.Response.Body = new MemoryStream();

        await tester.TestAsync(context, _port);

        // Nothing listens on plcPort: a second driver would answer "unreachable".
        context.Response.Body.Position = 0;
        Assert.Equal("ok", JsonDocument.Parse(context.Response.Body).RootElement.GetProperty("result").GetString());
        Assert.Equal(1, driver.ReachabilityChecks);
    }

    [Fact]
    public async Task ANamedDevice_IsTestedThroughItsOwnRunningDriverOnly()
    {
        var a = new ReachabilityDriver();
        var b = new ReachabilityDriver();
        await using var edge = EdgeTestHost.Create(
            EdgeTestHost.MultiDevice(new Dictionary<string, string?>
            {
                ["Edge:Channels:0:Name"] = "S7",
                ["Edge:Channels:0:Driver"] = "S7",
                ["Edge:Channels:0:Devices:0:Name"] = "A",
                ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
                ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1",
                ["Edge:Channels:0:Devices:1:Name"] = "B",
                ["Edge:Channels:0:Devices:1:Model"] = "S7-1200",
                ["Edge:Channels:0:Devices:1:Host"] = "127.0.0.2",
            }),
            services => services.AddSingleton<IndustrialIot.Edge.Drivers.IPlcDriverFactory>(
                new FakeDriverFactory(device => device.Name == "A" ? a : b)));
        SettingsPassword.Store(SettingsPassword.PathFor(edge.Services.GetRequiredService<EdgeSettingsFile>().Path), Password);
        var tester = edge.Services.GetRequiredService<PlcConnectionTester>();

        var (status, result) = await TestDirectAsync(tester, new JsonObject { ["password"] = Password, ["device"] = "b" });
        var (unknownStatus, unknown) = await TestDirectAsync(tester, new JsonObject { ["password"] = Password, ["device"] = "C" });

        Assert.Equal((int)HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.Equal((0, 1), (a.ReachabilityChecks, b.ReachabilityChecks));
        Assert.Equal((int)HttpStatusCode.BadRequest, unknownStatus);
        Assert.Contains("device", unknown.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisabledFx3u_IsTestedWithATemporaryMc1eDriver()
    {
        await using var adapter = new FakeMc1eServer();
        await using var edge = EdgeTestHost.Create(
            EdgeTestHost.MultiDevice(new Dictionary<string, string?>
            {
                ["Edge:Channels:0:Name"] = "Fx3",
                ["Edge:Channels:0:Driver"] = "Mc1E",
                ["Edge:Channels:0:Devices:0:Name"] = "Old",
                ["Edge:Channels:0:Devices:0:Model"] = "FX3U",
                ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1",
                ["Edge:Channels:0:Devices:0:Port"] = adapter.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Edge:Channels:0:Devices:0:Enabled"] = "false",
            }));
        SettingsPassword.Store(SettingsPassword.PathFor(edge.Services.GetRequiredService<EdgeSettingsFile>().Path), Password);
        var tester = edge.Services.GetRequiredService<PlcConnectionTester>();

        var (status, result) = await TestDirectAsync(tester, new JsonObject { ["password"] = Password, ["device"] = "Old" });

        Assert.Equal((int)HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.Equal(1, adapter.Connections);
        Assert.Empty(adapter.Frames); // a reachability test connects and sends nothing
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

    private async Task<(int Status, JsonElement Body)> TestDirectAsync(PlcConnectionTester tester, JsonObject body)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()));
        context.Response.Body = new MemoryStream();

        await tester.TestAsync(context, _port);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, JsonDocument.Parse(context.Response.Body).RootElement.Clone());
    }

    /// <summary>Answers reachable and counts how often it was asked.</summary>
    private sealed class ReachabilityDriver : IndustrialIot.Edge.Drivers.IPlcDriver
    {
        private int _checks;

        public int ReachabilityChecks => Volatile.Read(ref _checks);

        public Task<IReadOnlyList<IndustrialIot.Edge.Drivers.PlcReadResult>> ReadAsync(
            IReadOnlyList<IndustrialIot.Edge.Drivers.ResolvedTag> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IndustrialIot.Edge.Drivers.PlcReadResult>>([]);

        public Task WriteAsync(
            IndustrialIot.Edge.Drivers.Models.IPlcAddress address, IndustrialIot.Edge.Drivers.PlcDataType type, object value, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> IsReachableAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _checks);
            return Task.FromResult(true);
        }
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
