using System.Diagnostics;
using System.Globalization;
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
/// <c>POST /api/settings/test-connection</c> checks a PLC without saving: a saved one by name, or an unsaved one
/// described by driver, model and endpoint. A separate driver is made and disposed after the test, unless a running
/// device has exactly that connection: then its own driver answers, and the same endpoint with other settings is
/// refused rather than opening a second connection. Only the settings password holder, from the operator's page, one
/// test at a time and at most 10 a minute.
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
    public async Task Fx5u_AgainstAFakePlc_IsOk()
    {
        await using var plc = new FakeSlmpServer();
        await StartAsync();

        var (status, result) = await TestAsync(Proposed("Slmp3E", "FX5U", plc.Port));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.Equal(1, plc.Connections);
    }

    [Fact]
    public async Task Fx3u_AgainstAFakeAdapter_IsOk_AndSendsNothing()
    {
        await using var adapter = new FakeMc1eServer();
        await StartAsync();

        var (status, result) = await TestAsync(Proposed("Mc1E", "FX3U", adapter.Port));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.Equal(1, adapter.Connections);
        Assert.Empty(adapter.Frames);
    }

    [Theory]
    [InlineData("S7", "S7-1200")]
    [InlineData("Slmp3E", "FX5U")]
    [InlineData("Mc1E", "FX3U")]
    public async Task ClosedPort_IsUnreachable_WithinTheTimeoutPlusOneSecond(string driver, string model)
    {
        await StartAsync();
        var clock = Stopwatch.StartNew();

        var (status, result) = await TestAsync(Proposed(driver, model, ClosedPort()));

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
            ["settings"] = JsonNode.Parse($$"""
                {"Channels":[{"Name":"S7","Driver":"S7","Devices":[{"Name":"Line","Model":"S7-1200","Host":"127.0.0.1","Port":{{plc.Port}},"Tags":[]}]}]}
                """),
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

        // The host as typed, with spaces: still the PLC the poller reads.
        var (status, result) = await TestAsync(S7(plc.Port, host: " 127.0.0.1 "));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.Equal(accepted, plc.AcceptCount);
        Assert.Equal(1, plc.OpenConnections);
    }

    [Theory]
    [InlineData("A", "S7", "S7-1200")]
    [InlineData("B", "Slmp3E", "FX5U")]
    [InlineData("C", "Mc1E", "FX3U")]
    public async Task AProposedDeviceEqualToARunningOne_IsAnsweredByItsDriver(string name, string driver, string model)
    {
        // Nothing listens on these ports: a second driver would answer "unreachable".
        var (edge, drivers, ports) = MultiDeviceEdge();
        await using (edge)
        {
            var tester = edge.Services.GetRequiredService<PlcConnectionTester>();
            // A is running on "LocalHost": typed with spaces and in another case, it is still that PLC.
            var proposed = Proposed(driver, model, ports[name], host: name == "A" ? " localHOST " : HostOf(name));

            var (status, result) = await TestDirectAsync(tester, Body(proposed, Password));

            Assert.Equal((int)HttpStatusCode.OK, status);
            Assert.Equal("ok", result.GetProperty("result").GetString());
            Assert.Equal(1, drivers[name].ReachabilityChecks);
            Assert.Equal(0, drivers.Where(d => d.Key != name).Sum(d => d.Value.ReachabilityChecks));
        }
    }

    [Fact]
    public async Task TheEndpointOfARunningDevice_WithOtherSettings_Is409_AndNothingConnects()
    {
        var (edge, drivers, ports) = MultiDeviceEdge();
        await using (edge)
        {
            var tester = edge.Services.GetRequiredService<PlcConnectionTester>();

            var otherSlot = Proposed("S7", "S7-1200", ports["A"], host: "localhost");
            otherSlot["slot"] = 2;
            var otherRack = Proposed("S7", "S7-1200", ports["A"], host: "localhost");
            otherRack["rack"] = 1;
            var (slotStatus, slotResult) = await TestDirectAsync(tester, Body(otherSlot, Password));
            var (rackStatus, rackResult) = await TestDirectAsync(tester, Body(otherRack, Password));
            var (driverStatus, driverResult) = await TestDirectAsync(tester, Body(Proposed("Mc1E", "FX3U", ports["B"], host: HostOf("B")), Password));

            Assert.Equal((int)HttpStatusCode.Conflict, slotStatus);
            Assert.Equal("endpoint-in-use", slotResult.GetProperty("error").GetString());
            Assert.Equal((int)HttpStatusCode.Conflict, rackStatus);
            Assert.Equal("endpoint-in-use", rackResult.GetProperty("error").GetString());
            Assert.Equal((int)HttpStatusCode.Conflict, driverStatus);
            Assert.Equal("endpoint-in-use", driverResult.GetProperty("error").GetString());
            Assert.All(drivers.Values, d => Assert.Equal(0, d.ReachabilityChecks));
        }
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
    public async Task BodyOverOneMegabyte_Is413()
    {
        await StartAsync();

        using var response = await SendAsync("/api/settings/test-connection", HttpMethod.Post, new string(' ', SettingsService.DefaultMaxBodyBytes + 10) + "{}");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    public static TheoryData<string, string> InvalidProposals => new()
    {
        { "model-of-another-driver", "proposed:model" },
        { "fx3u-on-the-s7-port", "proposed:port" },
        { "fx5u-without-port", "proposed:port" },
        { "unknown-driver", "proposed:driver" },
        { "no-host", "proposed:host" },
        { "timeout-out-of-range", "proposed:timeoutMs" },
        { "unknown-key", "proposed:color" },
        { "schema-1-body", "plc" },
    };

    [Theory]
    [MemberData(nameof(InvalidProposals))]
    public async Task InvalidProposal_Is400_NamingTheKey_WithoutTheValue(string fault, string expectedPath)
    {
        await StartAsync();
        var body = fault switch
        {
            "model-of-another-driver" => Body(Proposed("S7", "FX5U", 5000, EchoedHost), Password),
            "fx3u-on-the-s7-port" => Body(Proposed("Mc1E", "FX3U", 102, EchoedHost), Password),
            "fx5u-without-port" => Body(Proposed("Slmp3E", "FX5U", null, EchoedHost), Password),
            "unknown-driver" => Body(Proposed("Modbus", "FX5U", 5000, EchoedHost), Password),
            "no-host" => Body(Proposed("S7", "S7-1200", 102, host: "  "), Password),
            "timeout-out-of-range" => Body(With(Proposed("Slmp3E", "FX5U", 5000, EchoedHost), "timeoutMs", 5), Password),
            "unknown-key" => Body(With(Proposed("S7", "S7-1200", 102, EchoedHost), "color", "red"), Password),
            "schema-1-body" => new JsonObject { ["password"] = Password, ["plc"] = new JsonObject { ["Protocol"] = "S7", ["Host"] = "10.9.8.7" } }.ToJsonString(),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        using var response = await SendAsync("/api/settings/test-connection", HttpMethod.Post, body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var paths = JsonDocument.Parse(text).RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains(expectedPath, paths);
        Assert.DoesNotContain(EchoedHost, text);
        Assert.DoesNotContain("Modbus", text);
        Assert.DoesNotContain("\"red\"", text);
    }

    [Fact]
    public async Task NullFields_AreLeftOut_SoTheModelDefaultsApply()
    {
        await StartAsync();
        var proposed = Proposed("S7", "S7-1200", port: null);
        proposed["port"] = null;
        proposed["rack"] = null;
        proposed["slot"] = null;

        // Port 102 by default; nothing listens there on the test machine.
        var (status, result) = await TestAsync(proposed);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("unreachable", result.GetProperty("result").GetString());
    }

    private const string EchoedHost = "10.9.8.7";

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

        var (status, result) = await TestDirectAsync(tester, new JsonObject { ["password"] = Password, ["device"] = "b" }.ToJsonString());
        var (unknownStatus, unknown) = await TestDirectAsync(tester, new JsonObject { ["password"] = Password, ["device"] = "C" }.ToJsonString());

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
                ["Edge:Channels:0:Devices:0:Port"] = adapter.Port.ToString(CultureInfo.InvariantCulture),
                ["Edge:Channels:0:Devices:0:Enabled"] = "false",
            }));
        SettingsPassword.Store(SettingsPassword.PathFor(edge.Services.GetRequiredService<EdgeSettingsFile>().Path), Password);
        var tester = edge.Services.GetRequiredService<PlcConnectionTester>();

        var (status, result) = await TestDirectAsync(tester, new JsonObject { ["password"] = Password, ["device"] = "Old" }.ToJsonString());

        Assert.Equal((int)HttpStatusCode.OK, status);
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.Equal(1, adapter.Connections);
        Assert.Empty(adapter.Frames); // a reachability test connects and sends nothing
    }

    private async Task StartAsync(int? plcPort = null, bool runAgentServices = false)
    {
        SettingsPassword.Store(Path.Combine(_contentRoot, "data", "edge.settings-password.json"), Password);
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge:EdgeId"] = "plc-test",
            ["Edge:Plc:Host"] = "127.0.0.1",
            ["Edge:Plc:Port"] = (plcPort ?? ClosedPort()).ToString(CultureInfo.InvariantCulture),
            ["Edge:Plc:TimeoutMs"] = "1000",
            ["Edge:Tags:0:TagId"] = "Line1.Speed",
            ["Edge:Tags:0:Address"] = "DB1.DBW2",
            ["Edge:Tags:0:DataType"] = "Int",
            ["Edge:Buffer:Path"] = "data/edge.db",
            ["Edge:Poll:IntervalMs"] = "200",
            ["Edge:Mqtt:Host"] = "127.0.0.1",
            ["Edge:Mqtt:Port"] = ClosedPort().ToString(CultureInfo.InvariantCulture),
            ["Edge:Mqtt:Username"] = "plc-test",
            ["Edge:Mqtt:Password"] = "test-only-password",
            ["Edge:Monitor:Enabled"] = "true",
            ["Edge:Monitor:Port"] = _port.ToString(CultureInfo.InvariantCulture),
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

    /// <summary>
    /// A, B and C (an S7-1200 on "LocalHost", an FX5U and an FX3U), each with a driver that answers reachable and counts
    /// the checks; nothing listens on their ports.
    /// </summary>
    private static (EdgeTestHost Edge, Dictionary<string, ReachabilityDriver> Drivers, Dictionary<string, int> Ports) MultiDeviceEdge()
    {
        var drivers = new Dictionary<string, ReachabilityDriver> { ["A"] = new(), ["B"] = new(), ["C"] = new() };
        var ports = new Dictionary<string, int> { ["A"] = ClosedPort(), ["B"] = ClosedPort(), ["C"] = ClosedPort() };
        var edge = EdgeTestHost.Create(
            EdgeTestHost.MultiDevice(new Dictionary<string, string?>
            {
                ["Edge:Channels:0:Name"] = "S7",
                ["Edge:Channels:0:Driver"] = "S7",
                ["Edge:Channels:0:Devices:0:Name"] = "A",
                ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
                ["Edge:Channels:0:Devices:0:Host"] = "LocalHost",
                ["Edge:Channels:0:Devices:0:Port"] = ports["A"].ToString(CultureInfo.InvariantCulture),
                ["Edge:Channels:1:Name"] = "Fx5",
                ["Edge:Channels:1:Driver"] = "Slmp3E",
                ["Edge:Channels:1:Devices:0:Name"] = "B",
                ["Edge:Channels:1:Devices:0:Model"] = "FX5U",
                ["Edge:Channels:1:Devices:0:Host"] = HostOf("B"),
                ["Edge:Channels:1:Devices:0:Port"] = ports["B"].ToString(CultureInfo.InvariantCulture),
                ["Edge:Channels:1:Devices:0:TimeoutMs"] = "1000",
                ["Edge:Channels:2:Name"] = "Fx3",
                ["Edge:Channels:2:Driver"] = "Mc1E",
                ["Edge:Channels:2:Devices:0:Name"] = "C",
                ["Edge:Channels:2:Devices:0:Model"] = "FX3U",
                ["Edge:Channels:2:Devices:0:Host"] = HostOf("C"),
                ["Edge:Channels:2:Devices:0:Port"] = ports["C"].ToString(CultureInfo.InvariantCulture),
                ["Edge:Channels:2:Devices:0:TimeoutMs"] = "1000",
            }),
            services => services.AddSingleton<IndustrialIot.Edge.Drivers.IPlcDriverFactory>(
                new FakeDriverFactory(device => drivers[device.Name])));
        SettingsPassword.Store(SettingsPassword.PathFor(edge.Services.GetRequiredService<EdgeSettingsFile>().Path), Password);
        return (edge, drivers, ports);
    }

    private static string HostOf(string name) => name switch
    {
        "B" => "127.0.0.2",
        "C" => "127.0.0.3",
        _ => "localhost",
    };

    private async Task<(int Status, JsonElement Body)> TestDirectAsync(PlcConnectionTester tester, string body)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
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
        With(Proposed("S7", "S7-1200", port, host), "timeoutMs", timeoutMs);

    private static JsonObject Proposed(string driver, string model, int? port, string host = "127.0.0.1")
    {
        var proposed = new JsonObject { ["driver"] = driver, ["model"] = model, ["host"] = host, ["timeoutMs"] = 1000 };
        if (port is { } value)
        {
            proposed["port"] = value;
        }

        return proposed;
    }

    private static JsonObject With(JsonObject node, string key, JsonNode value)
    {
        node[key] = value;
        return node;
    }

    private static string Body(JsonObject proposed, string password) =>
        new JsonObject { ["password"] = password, ["proposed"] = JsonNode.Parse(proposed.ToJsonString()) }.ToJsonString();

    private async Task<(HttpStatusCode Status, JsonElement Result)> TestAsync(JsonObject proposed, string password = Password)
    {
        using var response = await SendAsync("/api/settings/test-connection", HttpMethod.Post, Body(proposed, password));
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
