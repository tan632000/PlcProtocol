using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The settings API on the loopback monitor, run under the real supervisor with fake PLC drivers: it shows the Edge's
/// channels, devices and tags as one tree (a single-PLC Edge as its migrated device), checks a proposed tree alone with
/// the Edge's own validators, pins every error to its node, and saves a schema-2 file only for the holder of the
/// settings password, from the operator's own page. Every refusal leaves the file and the lockout untouched.
/// </summary>
public sealed class SettingsApiTests : IAsyncDisposable
{
    private const string Password = "operator-password-1";
    private readonly string _contentRoot = Directory.CreateTempSubdirectory("settings-api-").FullName;
    private readonly int _port = FreePort();
    private readonly int _plcPort = ClosedPort();
    private readonly LogCapture _logs = new();
    private readonly HttpClient _http;
    private IHost? _outer;

    public SettingsApiTests()
    {
        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
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
    public async Task Get_OnASinglePlcEdge_ShowsItAsDevicePlc1_WithLegacyTagIds()
    {
        await StartAsync();

        var get = await GetSettingsAsync();

        Assert.Equal("defaults", get.GetProperty("source").GetString());
        Assert.Equal(EdgeSettingsFile.NoFileRevision, get.GetProperty("revision").GetString());
        Assert.True(get.GetProperty("passwordSet").GetBoolean());
        Assert.False(get.GetProperty("commandsEnabled").GetBoolean());
        Assert.Empty(get.GetProperty("warnings").EnumerateArray());
        Assert.False(get.TryGetProperty("writableTags", out _));
        var channel = Assert.Single(get.GetProperty("settings").GetProperty("channels").EnumerateArray());
        Assert.Equal("S7", channel.GetProperty("driver").GetString());
        var device = Assert.Single(channel.GetProperty("devices").EnumerateArray());
        Assert.Equal("Plc1", device.GetProperty("name").GetString());
        Assert.Equal("S7-1200", device.GetProperty("model").GetString());
        Assert.True(device.GetProperty("legacyTagIds").GetBoolean());
        Assert.Equal(_plcPort, device.GetProperty("port").GetInt32());
        Assert.Equal(["Line1.Speed"], device.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Get_ListsTheModels_WithDefaultsHintsAndWriteGuard()
    {
        await StartAsync();

        var models = (await GetSettingsAsync()).GetProperty("models").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!);

        Assert.Equal(["S7-1200", "FX5U", "FX3U"], models.Keys);
        var s7 = models["S7-1200"];
        Assert.Equal("S7", s7.GetProperty("driver").GetString());
        Assert.Equal(102, s7.GetProperty("defaultPort").GetInt32());
        Assert.Equal(0, s7.GetProperty("defaultRack").GetInt32());
        Assert.Equal(1, s7.GetProperty("defaultSlot").GetInt32());
        Assert.Contains("DTL", s7.GetProperty("dataTypes").EnumerateArray().Select(t => t.GetString()));
        Assert.True(s7.GetProperty("writesAllowed").GetBoolean());
        var fx5u = models["FX5U"];
        Assert.Equal(JsonValueKind.Null, fx5u.GetProperty("defaultPort").ValueKind);
        Assert.Contains("GX Works3", fx5u.GetProperty("portHint").GetString());
        Assert.DoesNotContain("DTL", fx5u.GetProperty("dataTypes").EnumerateArray().Select(t => t.GetString()));
        Assert.False(models["FX3U"].GetProperty("writesAllowed").GetBoolean());
        Assert.Equal("Mc1E", models["FX3U"].GetProperty("driver").GetString());
    }

    [Fact]
    public async Task Get_OnASinglePlcS71500_WarnsThatTheCpuTypeIsNotKept()
    {
        await StartAsync(extra: new() { ["Edge:Plc:CpuType"] = "S71500" });

        var warnings = (await GetSettingsAsync()).GetProperty("warnings").EnumerateArray().Select(w => w.GetString());

        Assert.Equal(["cpuType-not-kept"], warnings);
    }

    [Fact]
    public async Task Put_TwoBrandsTwoPlcsEach_WritesSchema2_AndRunsEveryDevice()
    {
        await StartAsync();

        await SaveAndWaitAsync(TwoByTwo());

        using (var file = JsonDocument.Parse(File.ReadAllBytes(SettingsPath())))
        {
            Assert.Equal(2, file.RootElement.GetProperty("SchemaVersion").GetInt32());
        }

        var status = await GetJsonAsync("/api/status");
        Assert.Equal(
            ["Fx5uA", "Fx5uB", "S7A", "S7B"],
            status.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("name").GetString()).Order());
        Assert.Contains("S7A.Speed", status.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("tagId").GetString()));
        var get = await GetSettingsAsync();
        Assert.Equal("file", get.GetProperty("source").GetString());
        Assert.Equal(2, get.GetProperty("settings").GetProperty("channels").GetArrayLength());
    }

    [Theory]
    [InlineData("S7")]
    [InlineData("Slmp")]
    public async Task SinglePlcEdge_SavedUnchanged_PublishesTheSameTagIds(string protocol)
    {
        var extra = protocol == "Slmp"
            ? new Dictionary<string, string?> { ["Edge:Plc:Protocol"] = "Slmp", ["Edge:Plc:TimeoutMs"] = "1000", ["Edge:Tags:0:Address"] = "D100", ["Edge:Tags:1:TagId"] = "Line1.Count", ["Edge:Tags:1:Address"] = "D102", ["Edge:Tags:1:DataType"] = "Int" }
            : new Dictionary<string, string?> { ["Edge:Tags:1:TagId"] = "Line1.Count", ["Edge:Tags:1:Address"] = "DB1.DBW6", ["Edge:Tags:1:DataType"] = "Int" };
        await StartAsync(extra: extra);
        var before = TagIds(await GetJsonAsync("/api/status"));

        var settings = JsonNode.Parse((await GetSettingsAsync()).GetProperty("settings").GetRawText())!.AsObject();
        await SaveAndWaitAsync(settings);

        Assert.Equal(["Line1.Count", "Line1.Speed"], before);
        Assert.Equal(before, TagIds(await GetJsonAsync("/api/status")));
    }

    [Fact]
    public async Task Get_AfterASave_ShowsTheFile_NotTheAppsettingsChannels()
    {
        await StartAsync(multiDevice: true);

        await SaveAndWaitAsync(Tree(Channel("Mitsubishi", "Slmp3E", Device("Only", "FX5U", port: 5000, tags: [DTag("Count", "D10")]))));

        var channels = (await GetSettingsAsync()).GetProperty("settings").GetProperty("channels");
        var channel = Assert.Single(channels.EnumerateArray());
        Assert.Equal("Only", Assert.Single(channel.GetProperty("devices").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(["Only"], (await GetJsonAsync("/api/status")).GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task AppsettingsChannels_DoNotLeakIntoTheProposal()
    {
        // appsettings has Lab1 (FX5U on port 5000, three tags) at the same index: a proposed FX5U without a Port must
        // not inherit it, and the leftover tags must not be judged.
        await StartAsync(multiDevice: true);

        var errors = await ValidateErrorsAsync(Tree(Channel("Mitsubishi", "Slmp3E", Device("Lab1", "FX5U", port: null, tags: [DTag("Count", "D10")]))));

        Assert.Equal(["Edge:Channels:0:Devices:0:Port"], errors.Select(e => e.Path));
    }

    public static TheoryData<string, string> NodeErrors => new()
    {
        { "duplicate-device-name", "Edge:Channels:0:Devices:1:Name" },
        { "model-of-another-driver", "Edge:Channels:0:Devices:0:Model" },
        { "bad-s7-address", "Edge:Channels:1:Devices:0:Tags:5:Address" },
        { "fx3u-writes", "Edge:Channels:0:Devices:0:WritesEnabled" },
        { "empty-tag-object", "Edge:Channels:0:Devices:0:Tags:1" },
        { "tags-over-1000-on-a-device", "Edge:Channels:0:Devices:0:Tags" },
        { "tags-over-5000-in-total", "Edge:Channels:0:Devices:5:Tags" },
        { "unknown-device-key", "Edge:Channels:0:Devices:0:Color" },
        { "poll-out-of-range", "Edge:Channels:0:Devices:0:PollIntervalMs" },
        { "string-without-length", "Edge:Channels:0:Devices:0:Tags:0:Length" },
        { "channels-twice-in-another-case", "Edge:Channels:0" },
        { "device-key-twice-in-another-case", "Edge:Channels:0:Devices:0" },
    };

    [Theory]
    [MemberData(nameof(NodeErrors))]
    public async Task InvalidTree_IsRefused_NamingTheNode(string fault, string expectedPath)
    {
        await StartAsync();
        var digest = Digest();

        var proposed = fault switch
        {
            "duplicate-device-name" => Tree(Channel("Siemens", "S7", Device("Line", "S7-1200", host: "10.0.0.1"), Device("LINE", "S7-1200", host: "10.0.0.2"))),
            "model-of-another-driver" => Tree(Channel("Siemens", "S7", Device("Line", "FX5U", port: 5000))),
            "bad-s7-address" => Tree(
                Channel("Mitsubishi", "Slmp3E", Device("Press", "FX5U", port: 5000)),
                Channel("Siemens", "S7", Device("Line", "S7-1200", tags: [.. Enumerable.Range(0, 5).Select(i => DTag($"T{i}", $"DB1.DBW{i * 2}")), DTag("Bad", "D100")]))),
            "fx3u-writes" => Tree(Channel("Old", "Mc1E", Device("Fx3", "FX3U", port: 5551, writesEnabled: true))),
            "empty-tag-object" => Tree(Channel("Siemens", "S7", Device("Line", "S7-1200", tags: [DTag("A", "DB1.DBW0"), new JsonObject(), DTag("B", "DB1.DBW2")]))),
            "tags-over-1000-on-a-device" => Tree(Channel("Siemens", "S7", Device("Line", "S7-1200", tags: ManyTags(1001)))),
            "tags-over-5000-in-total" => Tree(Channel("Siemens", "S7", [.. Enumerable.Range(0, 6).Select(i => Device($"L{i}", "S7-1200", host: $"10.0.0.{i + 1}", tags: ManyTags(i < 5 ? 1000 : 1)))])),
            "unknown-device-key" => Tree(Channel("Siemens", "S7", With(Device("Line", "S7-1200"), "Color", "red"))),
            "poll-out-of-range" => Tree(Channel("Siemens", "S7", With(Device("Line", "S7-1200"), "PollIntervalMs", 5))),
            "string-without-length" => Tree(Channel("Siemens", "S7", Device("Line", "S7-1200", tags: [DTag("Batch", "DB1.DBB0", dataType: "String")]))),
            // The binder would merge both spellings, so the counts above could be passed with an empty first list.
            "channels-twice-in-another-case" => With(Tree(), "channels", new JsonArray(Channel("Siemens", "S7", Device("Line", "S7-1200", tags: ManyTags(1001))))),
            "device-key-twice-in-another-case" => Tree(Channel("Siemens", "S7", With(Device("Line", "S7-1200", tags: []), "tags", new JsonArray([.. ManyTags(1001)])))),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        using var response = await PutAsync(Body(await RevisionAsync(), Password, proposed));
        var errors = Errors(await ReadJsonAsync(response));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedPath, errors.Select(e => e.Path));
        Assert.DoesNotContain(errors, e => e.Path == "Edge:Channels");
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
        await Task.Delay(200);
        Assert.Equal(digest, Digest());
    }

    [Fact]
    public async Task ErrorsOnTwoDevices_AreBothListed()
    {
        await StartAsync();

        var errors = await ValidateErrorsAsync(Tree(
            Channel("Siemens", "S7", Device("Line", "S7-1300")),
            Channel("Mitsubishi", "Slmp3E", Device("Press", "FX5U", port: 5000, tags: [DTag("Ok", "D0"), DTag("Bad", "DB1.DBW0")]))));

        var paths = errors.Select(e => e.Path).ToList();
        Assert.Contains("Edge:Channels:0:Devices:0:Model", paths);
        Assert.Contains("Edge:Channels:1:Devices:0:Tags:1:Address", paths);
        Assert.DoesNotContain("Edge:Channels", paths);
    }

    public static TheoryData<string, HttpStatusCode> Refusals => new()
    {
        { "wrong-password", HttpStatusCode.Unauthorized },
        { "foreign-host", HttpStatusCode.BadRequest },
        { "text-plain", HttpStatusCode.UnsupportedMediaType },
        { "text-plain-json-param", HttpStatusCode.UnsupportedMediaType },
        { "evil-origin", HttpStatusCode.Forbidden },
        { "cross-site-fetch", HttpStatusCode.Forbidden },
        { "null-origin", HttpStatusCode.Forbidden },
        { "unknown-top-key", HttpStatusCode.BadRequest },
        { "unknown-settings-key", HttpStatusCode.BadRequest },
        { "schema-1-body", HttpStatusCode.BadRequest },
        { "stale-revision", HttpStatusCode.Conflict },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Refusal_HasItsStatus_AndLeavesTheFileUnchanged(string refusal, HttpStatusCode expected)
    {
        await StartAsync();
        var digest = Digest();
        var revision = await RevisionAsync();
        var good = OneDevice();

        var (body, contentType, headers) = refusal switch
        {
            "wrong-password" => (Body(revision, "not-the-password", good), "application/json", NoHeaders),
            "foreign-host" => (Body(revision, Password, good), "application/json", new() { ["Host"] = "evil.example" }),
            "text-plain" => (Body(revision, Password, good), "text/plain", NoHeaders),
            "text-plain-json-param" => (Body(revision, Password, good), "text/plain; x=application/json", NoHeaders),
            "evil-origin" => (Body(revision, Password, good), "application/json", new() { ["Origin"] = "http://evil.example" }),
            "cross-site-fetch" => (Body(revision, Password, good), "application/json", new() { ["Sec-Fetch-Site"] = "cross-site" }),
            "null-origin" => (Body(revision, Password, good), "application/json", new() { ["Origin"] = "null" }),
            "unknown-top-key" => (WithTopKey(Body(revision, Password, good), "extra"), "application/json", NoHeaders),
            "unknown-settings-key" => (Body(revision, Password, With(OneDevice(), "Mqtt", new JsonObject { ["Host"] = "x" })), "application/json", NoHeaders),
            "schema-1-body" => (Body(revision, Password, new JsonObject { ["Plc"] = new JsonObject { ["Host"] = "127.0.0.1" } }), "application/json", NoHeaders),
            "stale-revision" => (Body("0000", Password, good), "application/json", NoHeaders),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
        };

        using var response = await SendAsync(HttpMethod.Put, "/api/settings", body, contentType, headers);

        Assert.Equal(expected, response.StatusCode);
        await Task.Delay(200);
        Assert.Equal(digest, Digest());
        Assert.Equal(ApplyState.Applied, Restarter().Status.State);
        Assert.Null(Restarter().Status.ApplyId);
    }

    [Fact]
    public async Task ErrorResponses_NameKeyPaths_NotTheSubmittedValues()
    {
        await StartAsync();

        var text = await (await SendAsync(HttpMethod.Post, "/api/settings/validate", Body(await RevisionAsync(), null, Tree(
            Channel("Siemens", "S7",
                Device("Line", "S7-1200", host: "10.9.8.7", tags: [DTag("Far", "DB1.DBX9.9", dataType: "Bool")]),
                Device("Copy", "S7-1200", host: "10.9.8.7")))), "application/json", NoHeaders)).Content.ReadAsStringAsync();

        // Both values are refused, so their absence proves the response hides them.
        var paths = Errors(JsonDocument.Parse(text).RootElement).Select(e => e.Path).ToList();
        Assert.Contains("Edge:Channels:0:Devices:0:Tags:0:Address", paths);
        Assert.Contains("Edge:Channels:0:Devices:1:Host", paths);
        Assert.DoesNotContain("10.9.8.7", text);
        Assert.DoesNotContain("DB1.DBX9.9", text);
    }

    [Fact]
    public async Task WritePermissions_AreSavedAsSent()
    {
        await StartAsync();

        await SaveAndWaitAsync(Tree(Channel("Siemens", "S7",
            Device("Line", "S7-1200", writesEnabled: true, tags: [DTag("Setpoint", "DB1.DBW4", writable: true, min: 0, max: 1500)]))));

        var device = (await GetSettingsAsync()).GetProperty("settings").GetProperty("channels")[0].GetProperty("devices")[0];
        Assert.True(device.GetProperty("writesEnabled").GetBoolean());
        Assert.True(device.GetProperty("tags")[0].GetProperty("writable").GetBoolean());

        // The save carried what it changed against the running Edge (one PLC, writes off), logged once applied.
        var logged = _logs.Entries.Where(e => e.Contains("Write permission changed", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, logged.Count);
        Assert.Contains(logged, e => e.Contains("device Line off -> on"));
        Assert.Contains(logged, e => e.Contains("tag Line.Setpoint off -> on"));
    }

    [Fact]
    public async Task ValidPut_Is202_AndTheNewHostReportsThatApplyAsApplied()
    {
        await StartAsync();

        using var response = await PutAsync(Body(await RevisionAsync(), Password, OneDevice(pollIntervalMs: 500)));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var applyId = (await ReadJsonAsync(response)).GetProperty("applyId").GetString()!;

        var apply = await WaitForApplyAsync(applyId);
        Assert.Equal("applied", apply.GetProperty("state").GetString());
        Assert.Equal(Revision(File.ReadAllBytes(SettingsPath())), apply.GetProperty("revision").GetString());
        Assert.Equal(500, (await GetJsonAsync("/api/status")).GetProperty("pollIntervalMs").GetInt32());
    }

    [Fact]
    public async Task BodyOverFourMegabytes_Is413_AndOneOfTwoMegabytesIsRead()
    {
        await StartAsync();

        using var tooLarge = await PutAsync(new string(' ', SettingsService.MaxBodyBytes + 10) + "{}");
        using var large = await PutAsync(new string(' ', 2 * 1024 * 1024) + "{}");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, large.StatusCode);
        Assert.Equal(4 * 1024 * 1024, SettingsService.MaxBodyBytes);
    }

    [Fact]
    public async Task NoPasswordFile_Is403()
    {
        await StartAsync(withPassword: false);

        using var response = await PutAsync(Body(await RevisionAsync(), Password, OneDevice()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(File.Exists(SettingsPath()));
    }

    [Fact]
    public async Task FiveWrongPasswords_LockSaves_429()
    {
        await StartAsync();
        var revision = await RevisionAsync();

        for (var i = 0; i < 5; i++)
        {
            using var wrong = await PutAsync(Body(revision, "wrong-" + i, OneDevice()));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await PutAsync(Body(revision, Password, OneDevice()));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.False(File.Exists(SettingsPath()));
    }

    [Fact]
    public async Task Validate_ReturnsTheSameErrorsAsPut_AndWritesNothing()
    {
        await StartAsync();
        var revision = await RevisionAsync();
        var proposed = Tree(Channel("Siemens", "S7",
            Device("Line", "S7-1200", tags: [DTag("Bad", "D100")]),
            Device("Line", "S7-1200", host: "10.0.0.2")));

        using var validate = await SendAsync(HttpMethod.Post, "/api/settings/validate", Body(revision, null, proposed), "application/json", NoHeaders);
        using var put = await PutAsync(Body(revision, Password, proposed));

        var validated = await ReadJsonAsync(validate);
        var refused = await ReadJsonAsync(put);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        Assert.False(validated.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(validated.GetProperty("errors").GetRawText(), refused.GetProperty("errors").GetRawText());
        Assert.True(Errors(validated).Count >= 2);
        Assert.False(File.Exists(SettingsPath()));
    }

    [Fact]
    public async Task TwoConcurrentPuts_OneIs202_TheOther409()
    {
        await StartAsync();
        var revision = await RevisionAsync();

        var first = PutAsync(Body(revision, Password, OneDevice()));
        var second = PutAsync(Body(revision, Password, OneDevice(pollIntervalMs: 2000)));
        using var a = await first;
        using var b = await second;

        Assert.Equal(
            [HttpStatusCode.Accepted, HttpStatusCode.Conflict],
            new[] { a.StatusCode, b.StatusCode }.OrderBy(s => (int)s));
    }

    [Fact]
    public async Task GuardRefusals_DoNotCountTowardTheLockout()
    {
        await StartAsync();
        var revision = await RevisionAsync();
        var proposed = OneDevice();

        for (var i = 0; i < 4; i++)
        {
            using var wrong = await PutAsync(Body(revision, "wrong-" + i, proposed));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        for (var i = 0; i < 3; i++)
        {
            using var crossSite = await SendAsync(HttpMethod.Put, "/api/settings", Body(revision, "wrong-x", proposed), "application/json", new() { ["Origin"] = "http://evil.example" });
            using var plain = await SendAsync(HttpMethod.Put, "/api/settings", Body(revision, "wrong-y", proposed), "text/plain", NoHeaders);
            Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, plain.StatusCode);
        }

        using var right = await PutAsync(Body(revision, Password, proposed));
        Assert.Equal(HttpStatusCode.Accepted, right.StatusCode);
    }

    [Fact]
    public async Task ThePassword_AppearsInNoResponseAndNoLog()
    {
        await StartAsync();
        var revision = await RevisionAsync();
        const string Attempt = "attempted-secret-42";
        var bodies = new List<string>();

        foreach (var (password, proposed) in new[]
        {
            (Attempt, OneDevice()),
            (Attempt, Tree(Channel("Siemens", "S7", Device("Line", "S7-1200", tags: [DTag("Bad", "D100")])))),
            (Password, OneDevice()),
        })
        {
            using var response = await PutAsync(Body(revision, password, proposed));
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        await WaitUntilAsync(() => Restarter().Status.State != ApplyState.Applying, "apply finished");
        bodies.Add(await _http.GetStringAsync("/api/settings"));
        bodies.Add(await _http.GetStringAsync("/api/status"));
        foreach (var text in bodies.Concat(_logs.Entries))
        {
            Assert.DoesNotContain(Attempt, text);
            Assert.DoesNotContain(Password, text);
        }
    }

    [Fact]
    public async Task Lockout_SurvivesTheRebuildOfTheInnerHost()
    {
        // A right password resets the count, so the rebuild in between is requested directly, not through a save.
        await StartAsync();
        for (var i = 0; i < 4; i++)
        {
            using var wrong = await PutAsync(Body(await RevisionAsync(), "wrong-" + i, OneDevice()));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var restarter = Restarter();
        Assert.True(restarter.TryRequest(Encoding.UTF8.GetBytes(
            """{"SchemaVersion":2,"Edge":{"Channels":[{"Name":"S7","Driver":"S7","Devices":[{"Name":"B","Model":"S7-1200","Host":"127.0.0.1","Tags":[{"Name":"Two","Address":"DB1.DBW2","DataType":"Int"}]}]}]}}"""), out var applyId));
        restarter.Release(applyId);
        Assert.Equal("applied", (await WaitForApplyAsync(applyId)).GetProperty("state").GetString());

        var revision = await RevisionAsync();
        using (var fifth = await PutAsync(Body(revision, "wrong-4", OneDevice())))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, fifth.StatusCode);
        }

        using var locked = await PutAsync(Body(revision, Password, OneDevice()));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    private static readonly Dictionary<string, string> NoHeaders = [];

    private async Task StartAsync(bool withPassword = true, bool multiDevice = false, Dictionary<string, string?>? extra = null)
    {
        if (withPassword)
        {
            SettingsPassword.Store(Path.Combine(_contentRoot, "data", "edge.settings-password.json"), Password);
        }

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge:EdgeId"] = "settings-api-test",
            ["Edge:Buffer:Path"] = "data/edge.db",
            ["Edge:Mqtt:Host"] = "127.0.0.1",
            ["Edge:Mqtt:Port"] = ClosedPort().ToString(CultureInfo.InvariantCulture),
            ["Edge:Mqtt:Username"] = "settings-api-test",
            ["Edge:Mqtt:Password"] = "test-only-password",
            ["Edge:Commands:Enabled"] = "false",
            ["Edge:Monitor:Enabled"] = "true",
            ["Edge:Monitor:Port"] = _port.ToString(CultureInfo.InvariantCulture),
        };
        if (multiDevice)
        {
            // As in lab/multi: the channels come from appsettings.
            settings["Edge:Channels:0:Name"] = "Mitsubishi";
            settings["Edge:Channels:0:Driver"] = "Slmp3E";
            settings["Edge:Channels:0:Devices:0:Name"] = "Lab1";
            settings["Edge:Channels:0:Devices:0:Model"] = "FX5U";
            settings["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1";
            settings["Edge:Channels:0:Devices:0:Port"] = "5000";
            for (var i = 0; i < 3; i++)
            {
                settings[$"Edge:Channels:0:Devices:0:Tags:{i}:Name"] = $"Old{i}";
                settings[$"Edge:Channels:0:Devices:0:Tags:{i}:Address"] = $"D{200 + i}";
                settings[$"Edge:Channels:0:Devices:0:Tags:{i}:DataType"] = "Int";
            }

            settings["Edge:Channels:1:Name"] = "Siemens";
            settings["Edge:Channels:1:Driver"] = "S7";
            settings["Edge:Channels:1:Devices:0:Name"] = "Lab2";
            settings["Edge:Channels:1:Devices:0:Model"] = "S7-1200";
            settings["Edge:Channels:1:Devices:0:Host"] = "127.0.0.2";
        }
        else
        {
            settings["Edge:Plc:Host"] = "127.0.0.1";
            settings["Edge:Plc:Port"] = _plcPort.ToString(CultureInfo.InvariantCulture);
            settings["Edge:Plc:TimeoutMs"] = "500";
            settings["Edge:Poll:IntervalMs"] = "1000";
            settings["Edge:Tags:0:TagId"] = "Line1.Speed";
            settings["Edge:Tags:0:Address"] = "DB1.DBW2";
            settings["Edge:Tags:0:DataType"] = "Int";
        }

        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _contentRoot });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddEdgeSupervisor(options => options.ConfigureInner = services =>
            services.Replace(ServiceDescriptor.Singleton<IPlcDriverFactory>(new FakeDriverFactory(_ => new FakePlcDriver()))));
        _outer = builder.Build();
        await _outer.StartAsync();
    }

    private EdgeRestarter Restarter() => _outer!.Services.GetRequiredService<EdgeRestarter>();

    private async Task<string> SaveAndWaitAsync(JsonObject proposed)
    {
        using var response = await PutAsync(Body(await RevisionAsync(), Password, proposed));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, text);
        var applyId = JsonDocument.Parse(text).RootElement.GetProperty("applyId").GetString()!;
        Assert.Equal("applied", (await WaitForApplyAsync(applyId)).GetProperty("state").GetString());
        return applyId;
    }

    private async Task<List<(string Path, string Message)>> ValidateErrorsAsync(JsonObject proposed)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/settings/validate", Body(await RevisionAsync(), null, proposed), "application/json", NoHeaders);
        return Errors(await ReadJsonAsync(response));
    }

    private static List<(string Path, string Message)> Errors(JsonElement reply) =>
        [.. reply.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("path").GetString()!, e.GetProperty("message").GetString()!))];

    private static List<string> TagIds(JsonElement status) =>
        [.. status.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("tagId").GetString()!).Order(StringComparer.Ordinal)];

    private async Task<JsonElement> WaitForApplyAsync(string applyId)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var apply = (await GetJsonAsync("/api/status")).GetProperty("settingsApply");
                if (apply.GetProperty("applyId").GetString() == applyId && apply.GetProperty("state").GetString() != "applying")
                {
                    return apply;
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

    private async Task<string> RevisionAsync() => (await GetSettingsAsync()).GetProperty("revision").GetString()!;

    private Task<JsonElement> GetSettingsAsync() => GetJsonAsync("/api/settings");

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await _http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private Task<HttpResponseMessage> PutAsync(string body) =>
        SendAsync(HttpMethod.Put, "/api/settings", body, "application/json", NoHeaders);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string body, string contentType, Dictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(method, path) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        // Unvalidated: the guard must also see malformed or misleading media types.
        request.Content.Headers.Remove("Content-Type");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        foreach (var (name, value) in headers)
        {
            if (name == "Host")
            {
                request.Headers.Host = value;
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await _http.SendAsync(request);
    }

    private static string Body(string revision, string? password, JsonObject settings)
    {
        var body = new JsonObject { ["revision"] = revision, ["settings"] = JsonNode.Parse(settings.ToJsonString()) };
        if (password is not null)
        {
            body["password"] = password;
        }

        return body.ToJsonString();
    }

    private static string WithTopKey(string body, string key)
    {
        var node = JsonNode.Parse(body)!.AsObject();
        node[key] = 1;
        return node.ToJsonString();
    }

    private static JsonObject With(JsonObject node, string key, JsonNode value)
    {
        node[key] = value;
        return node;
    }

    private JsonObject OneDevice(int pollIntervalMs = 1000) =>
        Tree(Channel("Siemens", "S7", With(Device("Line", "S7-1200", tags: [DTag("Speed", "DB1.DBW2")]), "PollIntervalMs", pollIntervalMs)));

    private static JsonObject TwoByTwo() => Tree(
        Channel("Siemens", "S7",
            Device("S7A", "S7-1200", host: "10.0.0.1", tags: [DTag("Speed", "DB1.DBW2")]),
            Device("S7B", "S7-1200", host: "10.0.0.2", tags: [DTag("Temp", "DB1.DBD4", dataType: "Real")])),
        Channel("Mitsubishi", "Slmp3E",
            Device("Fx5uA", "FX5U", host: "10.0.0.3", port: 5000, tags: [DTag("Count", "D100")]),
            Device("Fx5uB", "FX5U", host: "10.0.0.4", port: 5000, tags: [DTag("Run", "M0", dataType: "Bool")])));

    private static JsonObject Tree(params JsonObject[] channels) => new() { ["Channels"] = new JsonArray([.. channels]) };

    private static JsonObject Channel(string name, string driver, params JsonObject[] devices) => new()
    {
        ["Name"] = name,
        ["Driver"] = driver,
        ["Devices"] = new JsonArray([.. devices]),
    };

    private static JsonObject Device(
        string name, string model, string host = "127.0.0.1", int? port = null, bool writesEnabled = false, JsonObject[]? tags = null)
    {
        var device = new JsonObject
        {
            ["Name"] = name,
            ["Model"] = model,
            ["Host"] = host,
            ["TimeoutMs"] = 1000,
            ["WritesEnabled"] = writesEnabled,
            ["Tags"] = new JsonArray([.. tags ?? [DTag("Speed", model == "S7-1200" ? "DB1.DBW2" : "D0")]]),
        };
        if (port is { } value)
        {
            device["Port"] = value;
        }

        return device;
    }

    private static JsonObject DTag(string name, string address, string dataType = "Int", bool writable = false, decimal? min = null, decimal? max = null)
    {
        var tag = new JsonObject { ["Name"] = name, ["Address"] = address, ["DataType"] = dataType, ["Writable"] = writable };
        if (min is { } low)
        {
            tag["Min"] = low;
        }

        if (max is { } high)
        {
            tag["Max"] = high;
        }

        return tag;
    }

    private static JsonObject[] ManyTags(int count) =>
        [.. Enumerable.Range(0, count).Select(i => DTag($"T{i}", $"DB1.DBW{i * 2}"))];

    private string SettingsPath() => Path.Combine(_contentRoot, "data", "edge.settings.json");

    private string Digest() => File.Exists(SettingsPath()) ? Revision(File.ReadAllBytes(SettingsPath())) : "absent";

    private static string Revision(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), $"timed out waiting for: {what}");
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

    private static int FreePort() => ClosedPort();

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyList<string> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new Logger(_entries);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + exception);
        }
    }
}
