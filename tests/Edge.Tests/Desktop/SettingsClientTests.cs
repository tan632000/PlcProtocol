using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.EdgeDesktop.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using static IndustrialIot.Edge.Tests.Desktop.StatusClientTests;

namespace IndustrialIot.Edge.Tests.Desktop;

/// <summary>
/// The desktop app's settings client against the real settings API, run under the real supervisor with fake PLC
/// drivers (as <see cref="SettingsApiTests"/>), plus the client's own rules a server cannot observe: what is never sent,
/// the exact body keys, and the timeouts. Every refusal must come back as its own result, classified by the reply's
/// <c>error</c> key, and the password must never leave the one request it was typed for.
/// </summary>
public sealed class SettingsClientTests : IAsyncDisposable
{
    private const string Password = "operator-password-1";

    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private readonly string _contentRoot = Directory.CreateTempSubdirectory("settings-client-").FullName;
    private readonly int _port = FreePort();
    private readonly int _plcPort = FreePort();
    private readonly HttpClient _http;
    private IHost? _outer;

    public SettingsClientTests()
    {
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
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
    public async Task Get_ReadsStrictly_AndRoundTripsThroughTheDraft()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);

        var raw = await _http.GetStringAsync("/api/settings");
        var strict = JsonSerializer.Deserialize<SettingsSnapshot>(raw, Strict)!;
        var load = await client.GetAsync();

        Assert.Null(load.Refusal);
        Assert.Equal(strict.Revision, load.Snapshot!.Revision);
        Assert.True(load.Snapshot.PasswordSet);
        Assert.Equal(["S7-1200", "FX5U", "FX3U"], load.Snapshot.Models.Select(m => m.Name));
        var draft = new ConfigDraft(load.Snapshot);
        Assert.Equal("Speed", draft.Channels[0].Devices[0].Tags[0].Name);
        var sent = JsonNode.Parse(draft.ToSettingsJson().ToJsonString())!;
        var got = JsonNode.Parse(raw)!["settings"]!;
        Assert.True(SameIgnoringKeyCase(got, sent), $"{got.ToJsonString()}\n{sent.ToJsonString()}");
    }

    [Theory]
    [InlineData("passwordSet")]
    [InlineData("models[0].writesAllowed")]
    [InlineData("settings.channels[0].devices[0].legacyTagIds")]
    [InlineData("settings.channels[0].devices[0].tags[0].dataType")]
    public async Task Get_RemovedProperty_FailsTheStrictRead(string path)
    {
        await StartAsync();
        var node = JsonNode.Parse(await _http.GetStringAsync("/api/settings"))!;
        var (parent, name) = Locate(node, path);

        parent.Remove(name);

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<SettingsSnapshot>(node.ToJsonString(), Strict));
    }

    [Fact]
    public async Task Validate_ReturnsErrorPaths_ThatTheDraftLocates()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);
        var draft = new ConfigDraft((await client.GetAsync()).Snapshot!);
        var badTag = draft.Channels[0].Devices[0].Tags[0];
        draft.Update(badTag, new DraftTag { Name = "Speed", Address = "not an address", DataType = "Int" });
        var twin = draft.AddChannel(new DraftChannel { Name = "Siemens", Driver = "S7" });
        draft.AddDevice(twin, new DraftDevice { Name = "Other", Model = "S7-1200", Host = "10.0.0.9" });

        var result = await client.ValidateAsync(draft);

        Assert.Null(result.Refusal);
        var located = result.Errors.Select(e => draft.Locate(e.Path)).ToList();
        Assert.Contains(located, l => l is { Tag: var t, Field: "Address" } && ReferenceEquals(t, badTag));
        Assert.Contains(located, l => l is { Device: null, Field: "Name" } && ReferenceEquals(l.Channel, twin));
    }

    [Fact]
    public async Task Save_IsAccepted_AndAppliedOnTheEdge()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);
        var draft = new ConfigDraft((await client.GetAsync()).Snapshot!);
        draft.AddTag(draft.Channels[0].Devices[0], new DraftTag { Name = "Temp", Address = "DB1.DBD4", DataType = "Real" });

        var save = await client.SaveAsync(draft, Password);

        Assert.Null(save.Refusal);
        Assert.Equal("applied", await WaitForApplyAsync(save.ApplyId!));
        var after = new ConfigDraft((await client.GetAsync()).Snapshot!);
        Assert.Equal(["Speed", "Temp"], after.Channels[0].Devices[0].Tags.Select(t => t.Name));
        Assert.NotEqual(draft.Revision, after.Revision);
    }

    [Fact]
    public async Task Save_WithAWrongPassword_IsWrongPassword()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);
        var draft = new ConfigDraft((await client.GetAsync()).Snapshot!);

        var save = await client.SaveAsync(draft, "not-the-password");

        Assert.Equal(RefusalKind.WrongPassword, save.Refusal!.Kind);
        Assert.Null(save.ApplyId);
    }

    [Fact]
    public async Task Save_WithoutAPasswordFile_IsPasswordNotSet()
    {
        await StartAsync(withPassword: false);
        using var client = new SettingsClient(_port);
        var draft = new ConfigDraft((await client.GetAsync()).Snapshot!);

        Assert.Equal(RefusalKind.PasswordNotSet, (await client.SaveAsync(draft, Password)).Refusal!.Kind);
    }

    [Fact]
    public async Task Save_AfterRepeatedWrongPasswords_IsLockedWithItsSeconds()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);
        var draft = new ConfigDraft((await client.GetAsync()).Snapshot!);

        SaveResult save;
        var attempts = 0;
        do
        {
            save = await client.SaveAsync(draft, "not-the-password");
        }
        while (save.Refusal!.Kind == RefusalKind.WrongPassword && ++attempts < 10);

        Assert.Equal(RefusalKind.Locked, save.Refusal.Kind);
        Assert.True(save.Refusal.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task Save_WithAnOldRevision_IsStaleRevision()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);
        var draft = new ConfigDraft((await client.GetAsync()).Snapshot!);
        draft.AddTag(draft.Channels[0].Devices[0], new DraftTag { Name = "Temp", Address = "DB1.DBD4", DataType = "Real" });
        Assert.Equal("applied", await WaitForApplyAsync((await client.SaveAsync(draft, Password)).ApplyId!));

        var again = await client.SaveAsync(draft, Password);

        Assert.Equal(RefusalKind.StaleRevision, again.Refusal!.Kind);
    }

    [Fact]
    public async Task TestConnection_ToAClosedPort_IsUnreachable()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);

        var test = await client.TestConnectionAsync(S7At(ClosedPort()), Password);

        Assert.Null(test.Refusal);
        Assert.False(test.Reachable);
        Assert.NotNull(test.ElapsedMs);
    }

    [Fact]
    public async Task TestConnection_RunningPlcWithAnotherRack_IsEndpointInUse()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);

        var test = await client.TestConnectionAsync(S7At(_plcPort) with { Rack = 1 }, Password);

        Assert.Equal(RefusalKind.EndpointInUse, test.Refusal!.Kind);
    }

    [Fact]
    public async Task TestConnection_WhileAnotherRuns_IsTestRunning()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);
        // Accepts the connection and never answers, so the first test holds the Edge's single test slot.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
        var accept = silent.AcceptTcpClientAsync();

        var first = client.TestConnectionAsync(S7At(silentPort) with { TimeoutMs = 3000 }, Password);
        using var held = await accept.WaitAsync(TimeSpan.FromSeconds(20));
        var second = await client.TestConnectionAsync(S7At(ClosedPort()), Password);

        Assert.Equal(RefusalKind.TestRunning, second.Refusal!.Kind);
        Assert.Null((await first).Refusal);
    }

    [Fact]
    public async Task TestConnection_ElevenInAMinute_IsRateLimited_NotLocked()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);
        var closed = ClosedPort();

        TestResult test;
        var attempts = 0;
        do
        {
            test = await client.TestConnectionAsync(S7At(closed), Password);
        }
        while (test.Refusal is null && ++attempts < 20);

        Assert.Equal(RefusalKind.RateLimited, test.Refusal!.Kind);
        Assert.Null(test.Refusal.RetryAfterSeconds);
        Assert.Equal(10, attempts);
    }

    [Fact]
    public async Task EmptyPassword_SendsNothing()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("nothing may be sent"));
        using var client = new SettingsClient(5099, handler);
        var draft = new ConfigDraft(Snapshot());

        Assert.Equal(RefusalKind.EmptyPassword, (await client.SaveAsync(draft, "")).Refusal!.Kind);
        Assert.Equal(RefusalKind.EmptyPassword, (await client.TestConnectionAsync(S7At(102), "")).Refusal!.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Save_GoesToLoopback_AsJson_WithLowerCaseTopKeys_AndNoOrigin()
    {
        string? body = null;
        var handler = new RecordingHandler(async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Reply(HttpStatusCode.Accepted, """{"applyId":"a1"}""");
        });
        using var client = new SettingsClient(5099, handler);
        var draft = new ConfigDraft(Snapshot());

        var save = await client.SaveAsync(draft, Password);

        Assert.Equal("a1", save.ApplyId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("http://127.0.0.1:5099/api/settings", request.RequestUri!.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.False(request.Headers.Contains("Origin"));
        var root = JsonNode.Parse(body!)!.AsObject();
        Assert.Equal(["revision", "password", "settings"], root.Select(p => p.Key));
        Assert.Equal("rev-1", root["revision"]!.GetValue<string>());
        Assert.NotNull(root["settings"]!["Channels"]);
    }

    [Fact]
    public async Task TestConnection_SendsTheProposedPlc_WithTheWebKeys()
    {
        string? body = null;
        var handler = new RecordingHandler(async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Reply(HttpStatusCode.OK, """{"result":"ok","elapsedMs":12.5}""");
        });
        using var client = new SettingsClient(5099, handler);

        var test = await client.TestConnectionAsync(S7At(102) with { Rack = 0, Slot = 1 }, Password);

        Assert.True(test.Reachable);
        Assert.Equal(12.5, test.ElapsedMs);
        Assert.Equal("http://127.0.0.1:5099/api/settings/test-connection", Assert.Single(handler.Requests).RequestUri!.ToString());
        var proposed = JsonNode.Parse(body!)!["proposed"]!.AsObject();
        Assert.Equal(["driver", "model", "host", "port", "rack", "slot", "timeoutMs"], proposed.Select(p => p.Key));
    }

    [Theory]
    [InlineData(401, """{"error":"wrong-password"}""", RefusalKind.WrongPassword, null)]
    [InlineData(403, """{"error":"password-not-set"}""", RefusalKind.PasswordNotSet, null)]
    [InlineData(429, """{"error":"locked","retryAfterSeconds":7}""", RefusalKind.Locked, 7)]
    [InlineData(429, """{"error":"rate-limited"}""", RefusalKind.RateLimited, null)]
    [InlineData(409, """{"error":"stale-revision","revision":"r2"}""", RefusalKind.StaleRevision, null)]
    [InlineData(409, """{"error":"apply-in-progress"}""", RefusalKind.ApplyInProgress, null)]
    [InlineData(409, """{"error":"endpoint-in-use","message":"m"}""", RefusalKind.EndpointInUse, null)]
    [InlineData(409, """{"error":"test-running"}""", RefusalKind.TestRunning, null)]
    [InlineData(413, """{"error":"too-large"}""", RefusalKind.TooLarge, null)]
    [InlineData(400, """{"ok":false,"errors":[{"path":"proposed:port","message":"m"}]}""", RefusalKind.Invalid, null)]
    [InlineData(500, "", RefusalKind.Failed, null)]
    public async Task Refusals_AreClassifiedByTheErrorKey(int status, string json, RefusalKind kind, int? seconds)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Reply((HttpStatusCode)status, json)));
        using var client = new SettingsClient(5099, handler);

        var save = await client.SaveAsync(new ConfigDraft(Snapshot()), Password);
        var test = await client.TestConnectionAsync(S7At(102), Password);

        foreach (var refusal in new[] { save.Refusal!, test.Refusal! })
        {
            Assert.Equal(kind, refusal.Kind);
            Assert.Equal(seconds, refusal.RetryAfterSeconds);
        }

        if (kind == RefusalKind.Invalid)
        {
            Assert.Equal("proposed:port", Assert.Single(test.Refusal!.Errors).Path);
        }

        if (kind == RefusalKind.Failed)
        {
            Assert.Equal("HTTP 500", save.Refusal!.Detail);
        }
    }

    [Fact]
    public async Task ValidateErrors_AreErrorsNotARefusal()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK,
            """{"ok":false,"errors":[{"path":"Edge:Channels:0:Name","message":"m"}]}""")));
        using var client = new SettingsClient(5099, handler);

        var result = await client.ValidateAsync(new ConfigDraft(Snapshot()));

        Assert.Null(result.Refusal);
        Assert.Equal("Edge:Channels:0:Name", Assert.Single(result.Errors).Path);
        Assert.Equal("http://127.0.0.1:5099/api/settings/validate", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Theory]
    [InlineData(202, """{"applyId":"a1"}""")]
    [InlineData(200, """{"result":"ok","elapsedMs":3}""")]
    [InlineData(409, """{"error":"endpoint-in-use","message":"save first"}""")]
    [InlineData(401, """{"error":"wrong-password"}""")]
    [InlineData(0, "")]
    public async Task NoPasswordInAnyResult(int status, string json)
    {
        var handler = new RecordingHandler((_, _) => status == 0
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(Reply((HttpStatusCode)status, json)));
        using var client = new SettingsClient(5099, handler);

        var save = await client.SaveAsync(new ConfigDraft(Snapshot()), Password);
        var test = await client.TestConnectionAsync(S7At(102), Password);

        foreach (var result in new object?[] { save, save.Refusal, test, test.Refusal }.OfType<object>())
        {
            Assert.DoesNotContain(Password, result.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Password, JsonSerializer.Serialize(result, result.GetType()), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(404, "[]")]
    [InlineData(400, "42")]
    [InlineData(200, "[]")]
    [InlineData(200, "\"x\"")]
    [InlineData(200, "not json")]
    public async Task OddReplies_AreFailed_NeverAnException(int status, string json)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Reply((HttpStatusCode)status, json)));
        using var client = new SettingsClient(5099, handler);
        var draft = new ConfigDraft(Snapshot());

        Assert.Equal(RefusalKind.Failed, (await client.GetAsync()).Refusal!.Kind);
        Assert.Equal(RefusalKind.Failed, (await client.SaveAsync(draft, Password)).Refusal!.Kind);
        Assert.Equal(RefusalKind.Failed, (await client.TestConnectionAsync(S7At(102), Password)).Refusal!.Kind);
        if (status != 200)
        {
            Assert.Equal(RefusalKind.Failed, (await client.ValidateAsync(draft)).Refusal!.Kind);
        }
    }

    [Fact]
    public async Task Get_WithoutSettingsOrModels_IsUnreadable()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Reply(HttpStatusCode.OK, """{"revision":"r"}""")));
        using var client = new SettingsClient(5099, handler);

        var load = await client.GetAsync();

        Assert.Null(load.Snapshot);
        Assert.Equal("dữ liệu không đọc được", load.Refusal!.Detail);
    }

    [Fact]
    public async Task Save_OfAnInvalidDraft_IsInvalidWithPaths()
    {
        await StartAsync();
        using var client = new SettingsClient(_port);
        var draft = new ConfigDraft((await client.GetAsync()).Snapshot!);
        draft.Update(draft.Channels[0].Devices[0].Tags[0], new DraftTag { Name = "Speed", Address = "not an address", DataType = "Int" });

        var save = await client.SaveAsync(draft, Password);

        Assert.Equal(RefusalKind.Invalid, save.Refusal!.Kind);
        Assert.Contains(save.Refusal.Errors, e => draft.Locate(e.Path) is { Field: "Address" });
    }

    [Fact]
    public async Task GetAndValidate_TimeOutAfter30Seconds()
    {
        var time = new FakeTimeProvider();
        using var client = new SettingsClient(5099, Hanging(), time);

        var get = client.GetAsync();
        var validate = client.ValidateAsync(new ConfigDraft(Snapshot()));
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(await CompletesAsync(get));
        Assert.False(await CompletesAsync(validate));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await CompletesAsync(get));
        Assert.True(await CompletesAsync(validate));
        Assert.Equal("quá 30 giây không trả lời", (await get).Refusal!.Detail);
        Assert.Equal("quá 30 giây không trả lời", (await validate).Refusal!.Detail);
    }

    [Fact]
    public async Task Save_TimesOutAfter30Seconds()
    {
        var time = new FakeTimeProvider();
        using var client = new SettingsClient(5099, Hanging(), time);

        var save = client.SaveAsync(new ConfigDraft(Snapshot()), Password);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(await CompletesAsync(save));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await CompletesAsync(save));
        Assert.Equal("quá 30 giây không trả lời", (await save).Refusal!.Detail);
    }

    [Fact]
    public async Task TestConnection_TimesOutAfterItsTimeoutPlusFiveSeconds()
    {
        var time = new FakeTimeProvider();
        using var client = new SettingsClient(5099, Hanging(), time);

        var test = client.TestConnectionAsync(S7At(102) with { TimeoutMs = 1000 }, Password);
        time.Advance(TimeSpan.FromMilliseconds(5900));
        Assert.False(await CompletesAsync(test));
        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(await CompletesAsync(test));
        Assert.Equal(RefusalKind.Failed, (await test).Refusal!.Kind);
    }

    internal static SettingsSnapshot Snapshot() => new(
        "rev-1",
        "file",
        new ChannelTree([new ChannelSettings("Siemens", "S7", [
            new DeviceSettings("Line", "S7-1200", "10.0.0.1", null, null, null, 3000, 1000, true, false, false,
                [new TagSettings("Speed", "DB1.DBW2", "Int", null, false, null, null, null)])])]),
        [new PlcModelInfo("S7-1200", "S7", 102, 0, 1, null, ["Bool", "Int"], true)],
        [],
        false,
        true);

    private static PlcConnection S7At(int port) => new("S7", "S7-1200", "127.0.0.1", port, null, null, 1000);

    private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static RecordingHandler Hanging() => new(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    });

    private static async Task<bool> CompletesAsync(Task task) =>
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(500))) == task;

    private static bool SameIgnoringKeyCase(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(p =>
            y.FirstOrDefault(q => string.Equals(q.Key, p.Key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } match
            && SameIgnoringKeyCase(p.Value, match.Value)),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(pair => SameIgnoringKeyCase(pair.First, pair.Second)),
        (JsonValue x, JsonValue y) => x.ToJsonString() == y.ToJsonString(),
        _ => false,
    };

    private static (JsonObject Parent, string Name) Locate(JsonNode root, string path)
    {
        var parts = path.Split('.');
        var node = root;
        foreach (var part in parts[..^1])
        {
            var bracket = part.IndexOf('[', StringComparison.Ordinal);
            node = bracket < 0
                ? node[part]!
                : node[part[..bracket]]![int.Parse(part[(bracket + 1)..^1], CultureInfo.InvariantCulture)]!;
        }

        return (node.AsObject(), parts[^1]);
    }

    private async Task StartAsync(bool withPassword = true)
    {
        if (withPassword)
        {
            SettingsPassword.Store(Path.Combine(_contentRoot, "data", "edge.settings-password.json"), Password);
        }

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge:EdgeId"] = "settings-client-test",
            ["Edge:Buffer:Path"] = "data/edge.db",
            ["Edge:Mqtt:Host"] = "127.0.0.1",
            ["Edge:Mqtt:Port"] = ClosedPort().ToString(CultureInfo.InvariantCulture),
            ["Edge:Mqtt:Username"] = "settings-client-test",
            ["Edge:Mqtt:Password"] = "test-only-password",
            ["Edge:Commands:Enabled"] = "false",
            ["Edge:Monitor:Enabled"] = "true",
            ["Edge:Monitor:Port"] = _port.ToString(CultureInfo.InvariantCulture),
            ["Edge:Channels:0:Name"] = "Siemens",
            ["Edge:Channels:0:Driver"] = "S7",
            ["Edge:Channels:0:Devices:0:Name"] = "Line",
            ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
            ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1",
            ["Edge:Channels:0:Devices:0:Port"] = _plcPort.ToString(CultureInfo.InvariantCulture),
            ["Edge:Channels:0:Devices:0:TimeoutMs"] = "1000",
            ["Edge:Channels:0:Devices:0:Tags:0:Name"] = "Speed",
            ["Edge:Channels:0:Devices:0:Tags:0:Address"] = "DB1.DBW2",
            ["Edge:Channels:0:Devices:0:Tags:0:DataType"] = "Int",
        };

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _contentRoot });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddEdgeSupervisor(options => options.ConfigureInner = services =>
            services.Replace(ServiceDescriptor.Singleton<IPlcDriverFactory>(new FakeDriverFactory(_ => new FakePlcDriver()))));
        _outer = builder.Build();
        await _outer.StartAsync();
    }

    private async Task<string> WaitForApplyAsync(string applyId)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var json = JsonDocument.Parse(await _http.GetStringAsync("/api/status"));
                var apply = json.RootElement.GetProperty("settingsApply");
                if (apply.GetProperty("applyId").GetString() == applyId && apply.GetProperty("state").GetString() != "applying")
                {
                    return apply.GetProperty("state").GetString()!;
                }
            }
            catch (HttpRequestException)
            {
                // The monitor is down while the inner host is rebuilt.
            }

            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), $"apply {applyId} did not finish");
            await Task.Delay(50);
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

    private static int FreePort() => ClosedPort();
}
