using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The settings API on the loopback monitor, run under the real supervisor: it reads the effective settings, checks
/// proposed ones with the Edge's own validators and the writable rule, and saves and applies them only for the holder
/// of the settings password, from the operator's own page. Every refusal leaves the file and the lockout untouched.
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
    public async Task Get_ShowsTheDefaults_ThenTheSavedFile()
    {
        await StartAsync();

        var before = await GetSettingsAsync();
        Assert.Equal("defaults", before.GetProperty("source").GetString());
        Assert.Equal(EdgeSettingsFile.NoFileRevision, before.GetProperty("revision").GetString());
        Assert.True(before.GetProperty("passwordSet").GetBoolean());
        Assert.Equal("S7", before.GetProperty("settings").GetProperty("plc").GetProperty("protocol").GetString());

        var applyId = await SaveAndWaitAsync(Proposed(tags: [Tag("New.One", "DB5.DBW0")]));

        var after = await GetSettingsAsync();
        Assert.Equal("file", after.GetProperty("source").GetString());
        Assert.Equal(Revision(File.ReadAllBytes(SettingsPath())), after.GetProperty("revision").GetString());
        Assert.Equal("New.One", after.GetProperty("settings").GetProperty("tags")[0].GetProperty("tagId").GetString());
        Assert.NotEmpty(applyId);
    }

    [Fact]
    public async Task ValidPut_Is202_AndTheNewHostReportsThatApplyAsApplied()
    {
        await StartAsync();

        using var response = await PutAsync(Body(await RevisionAsync(), Password, Proposed(tags: [Tag("New.One", "DB5.DBW0")], intervalMs: 500)));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var applyId = (await ReadJsonAsync(response)).GetProperty("applyId").GetString()!;

        var apply = await WaitForApplyAsync(applyId);
        Assert.Equal("applied", apply.GetProperty("state").GetString());
        Assert.Equal(Revision(File.ReadAllBytes(SettingsPath())), apply.GetProperty("revision").GetString());
        var status = await GetJsonAsync("/api/status");
        Assert.Equal(500, status.GetProperty("pollIntervalMs").GetInt32());
    }

    public static TheoryData<string, HttpStatusCode> Refusals => new()
    {
        { "wrong-password", HttpStatusCode.Unauthorized },
        { "foreign-host", HttpStatusCode.BadRequest },
        { "text-plain", HttpStatusCode.UnsupportedMediaType },
        { "text-plain-json-param", HttpStatusCode.UnsupportedMediaType },
        { "evil-origin", HttpStatusCode.Forbidden },
        { "cross-site-fetch", HttpStatusCode.Forbidden },
        { "unknown-top-key", HttpStatusCode.BadRequest },
        { "unknown-settings-key", HttpStatusCode.BadRequest },
        { "duplicate-tag", HttpStatusCode.BadRequest },
        { "bad-address", HttpStatusCode.BadRequest },
        { "slmp-with-commands", HttpStatusCode.BadRequest },
        { "stale-revision", HttpStatusCode.Conflict },
        { "writable-true", HttpStatusCode.BadRequest },
        { "writable-address-changed", HttpStatusCode.BadRequest },
        { "writable-min-changed", HttpStatusCode.BadRequest },
        { "writable-max-changed", HttpStatusCode.BadRequest },
        { "writable-datatype-changed", HttpStatusCode.BadRequest },
        { "writable-tagid-case", HttpStatusCode.BadRequest },
        { "host-changed-with-writable", HttpStatusCode.BadRequest },
        { "port-changed-with-writable", HttpStatusCode.BadRequest },
        { "protocol-changed-with-writable", HttpStatusCode.BadRequest },
        { "rack-changed-with-writable", HttpStatusCode.BadRequest },
        { "null-origin", HttpStatusCode.Forbidden },
        { "empty-tag-object", HttpStatusCode.BadRequest },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Refusal_HasItsStatus_AndLeavesTheFileUnchanged(string refusal, HttpStatusCode expected)
    {
        await StartAsync(withWritable: true, commandsEnabled: refusal == "slmp-with-commands");
        var digest = Digest();
        var revision = await RevisionAsync();
        var keep = WritableTag();

        var (body, contentType, headers) = refusal switch
        {
            "wrong-password" => (Body(revision, "not-the-password", Proposed(tags: [keep])), "application/json", NoHeaders),
            "foreign-host" => (Body(revision, Password, Proposed(tags: [keep])), "application/json", new() { ["Host"] = "evil.example" }),
            "text-plain" => (Body(revision, Password, Proposed(tags: [keep])), "text/plain", NoHeaders),
            "text-plain-json-param" => (Body(revision, Password, Proposed(tags: [keep])), "text/plain; x=application/json", NoHeaders),
            "evil-origin" => (Body(revision, Password, Proposed(tags: [keep])), "application/json", new() { ["Origin"] = "http://evil.example" }),
            "cross-site-fetch" => (Body(revision, Password, Proposed(tags: [keep])), "application/json", new() { ["Sec-Fetch-Site"] = "cross-site" }),
            "unknown-top-key" => (WithTopKey(Body(revision, Password, Proposed(tags: [keep])), "extra"), "application/json", NoHeaders),
            "unknown-settings-key" => (Body(revision, Password, WithKey(Proposed(tags: [keep]), "Mqtt", new JsonObject { ["Host"] = "x" })), "application/json", NoHeaders),
            "duplicate-tag" => (Body(revision, Password, Proposed(tags: [keep, Tag("A.Dup", "DB5.DBW0"), Tag("a.dup", "DB5.DBW2")])), "application/json", NoHeaders),
            "bad-address" => (Body(revision, Password, Proposed(tags: [keep, Tag("A.Bad", "D100")])), "application/json", NoHeaders),
            "slmp-with-commands" => (Body(revision, Password, Proposed(protocol: "Slmp", port: 5000, timeoutMs: 1000, tags: [Tag("F.D100", "D100")])), "application/json", NoHeaders),
            "stale-revision" => (Body("0000", Password, Proposed(tags: [keep])), "application/json", NoHeaders),
            "writable-true" => (Body(revision, Password, Proposed(tags: [keep, Tag("A.New", "DB5.DBW0", writable: true)])), "application/json", NoHeaders),
            "writable-address-changed" => (Body(revision, Password, Proposed(tags: [WritableTag(address: "DB1.DBW10")])), "application/json", NoHeaders),
            "writable-min-changed" => (Body(revision, Password, Proposed(tags: [WritableTag(min: 1)])), "application/json", NoHeaders),
            "writable-max-changed" => (Body(revision, Password, Proposed(tags: [WritableTag(max: 2000)])), "application/json", NoHeaders),
            "writable-datatype-changed" => (Body(revision, Password, Proposed(tags: [WritableTag(dataType: "Word")])), "application/json", NoHeaders),
            "writable-tagid-case" => (Body(revision, Password, Proposed(tags: [WritableTag(tagId: "line1.setpoint")])), "application/json", NoHeaders),
            "host-changed-with-writable" => (Body(revision, Password, Proposed(host: "10.0.0.9", tags: [keep])), "application/json", NoHeaders),
            "port-changed-with-writable" => (Body(revision, Password, Proposed(port: 1102, tags: [keep])), "application/json", NoHeaders),
            "protocol-changed-with-writable" => (Body(revision, Password, Proposed(protocol: "Slmp", port: 5000, timeoutMs: 1000, tags: [keep])), "application/json", NoHeaders),
            "rack-changed-with-writable" => (Body(revision, Password, WithPlcKey(Proposed(tags: [keep]), "Rack", 1)), "application/json", NoHeaders),
            "null-origin" => (Body(revision, Password, Proposed(tags: [keep])), "application/json", new() { ["Origin"] = "null" }),
            "empty-tag-object" => (Body(revision, Password, Proposed(tags: [keep, new JsonObject()])), "application/json", NoHeaders),
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
        await StartAsync(withWritable: true);
        var revision = await RevisionAsync();

        // A read-only tag on the writable tag's address (overlap), and an SLMP address out of range: validator text
        // for both names the address, the response must not.
        using var overlap = await PutAsync(Body(revision, Password, Proposed(tags: [WritableTag(), Tag("X.Shadow", "DB1.DBW4")])));
        using var range = await SendAsync(HttpMethod.Post, "/api/settings/validate",
            Body(revision, null, Proposed(protocol: "Slmp", port: 5000, timeoutMs: 1000, tags: [Tag("F.Far", "D99999")])), "application/json", NoHeaders);

        var overlapText = await overlap.Content.ReadAsStringAsync();
        var rangeText = await range.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, overlap.StatusCode);
        Assert.DoesNotContain("DB1.DBW4", overlapText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X.Shadow", overlapText);
        Assert.Contains("\"Edge:Tags:", overlapText);
        Assert.DoesNotContain("D99999", rangeText);
        Assert.Contains("Edge:Tags", rangeText);

        // A value that looks like a key: the binder names the value first, the path must still be the real key.
        using var typed = await SendAsync(HttpMethod.Post, "/api/settings/validate",
            Body(revision, null, Proposed(tags: [WritableTag(), Tag("A.Odd", "DB5.DBW0", dataType: "Edge:Fake:Key")])), "application/json", NoHeaders);
        var typedErrors = (await ReadJsonAsync(typed)).GetProperty("errors");
        Assert.StartsWith("Edge:Tags", typedErrors[0].GetProperty("path").GetString());
        Assert.DoesNotContain("Edge:Fake:Key", typedErrors.GetRawText());
    }

    [Fact]
    public async Task BodyOverOneMegabyte_Is413()
    {
        await StartAsync();

        using var response = await PutAsync(new string(' ', SettingsService.MaxBodyBytes + 10) + "{}");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task NoPasswordFile_Is403()
    {
        await StartAsync(withPassword: false);

        using var response = await PutAsync(Body(await RevisionAsync(), Password, Proposed(tags: [Tag("New.One", "DB5.DBW0")])));

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
            using var wrong = await PutAsync(Body(revision, "wrong-" + i, Proposed(tags: [Tag("New.One", "DB5.DBW0")])));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await PutAsync(Body(revision, Password, Proposed(tags: [Tag("New.One", "DB5.DBW0")])));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.False(File.Exists(SettingsPath()));
    }

    [Fact]
    public async Task DeletingAWritableTag_Is202_AndThenThePlcMayChange()
    {
        await StartAsync(withWritable: true);

        await SaveAndWaitAsync(Proposed(tags: [Tag("A.One", "DB5.DBW0")]));
        Assert.Empty((await GetSettingsAsync()).GetProperty("writableTags").EnumerateArray());

        await SaveAndWaitAsync(Proposed(host: "10.0.0.9", tags: [Tag("A.One", "DB5.DBW0")]));
        Assert.Equal("10.0.0.9", (await GetSettingsAsync()).GetProperty("settings").GetProperty("plc").GetProperty("host").GetString());
    }

    [Fact]
    public async Task KeptWritableTag_SentBackAsGetReturnedIt_StaysWritable()
    {
        await StartAsync(withWritable: true);

        // Exactly what the page does: send the writable tag back with Writable=false and its Min/Max; edit another tag.
        var current = (await GetSettingsAsync()).GetProperty("settings").GetProperty("tags");
        var sentBack = current.EnumerateArray().Select(t => new JsonObject
        {
            ["TagId"] = t.GetProperty("tagId").GetString(),
            ["Address"] = t.GetProperty("address").GetString(),
            ["DataType"] = t.GetProperty("dataType").GetString(),
            ["Writable"] = false,
            ["Min"] = t.GetProperty("min").ValueKind == JsonValueKind.Null ? null : t.GetProperty("min").GetDecimal(),
            ["Max"] = t.GetProperty("max").ValueKind == JsonValueKind.Null ? null : t.GetProperty("max").GetDecimal(),
        }).ToList();
        sentBack[1]["Address"] = "DB2.DBW0";

        await SaveAndWaitAsync(Proposed(tags: [.. sentBack]));

        var after = await GetSettingsAsync();
        Assert.Equal(["Line1.Setpoint"], after.GetProperty("writableTags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("DB2.DBW0", after.GetProperty("settings").GetProperty("tags")[1].GetProperty("address").GetString());
    }

    [Fact]
    public async Task Validate_ReturnsTheSameErrorsAsPut_AndWritesNothing()
    {
        await StartAsync(withWritable: true);
        var revision = await RevisionAsync();
        var proposed = Proposed(host: "10.0.0.9", tags: [WritableTag(address: "DB1.DBW10"), Tag("A.Bad", "D100")]);

        using var validate = await SendAsync(HttpMethod.Post, "/api/settings/validate", Body(revision, null, proposed), "application/json", NoHeaders);
        using var put = await PutAsync(Body(revision, Password, proposed));

        var validated = await ReadJsonAsync(validate);
        var refused = await ReadJsonAsync(put);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        Assert.False(validated.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(validated.GetProperty("errors").GetRawText(), refused.GetProperty("errors").GetRawText());
        // Both the writable-rule error (the changed writable tag) and the validator error (D100 is not an S7 address).
        var paths = validated.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains("Edge:Tags:0", paths);
        Assert.True(paths.Count >= 2, string.Join(", ", paths));
        Assert.False(File.Exists(SettingsPath()));
    }

    [Fact]
    public async Task TwoConcurrentPuts_OneIs202_TheOther409()
    {
        await StartAsync();
        var revision = await RevisionAsync();

        var first = PutAsync(Body(revision, Password, Proposed(tags: [Tag("New.One", "DB5.DBW0")])));
        var second = PutAsync(Body(revision, Password, Proposed(tags: [Tag("New.Two", "DB5.DBW0")])));
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
        var proposed = Proposed(tags: [Tag("New.One", "DB5.DBW0")]);

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
            (Attempt, Proposed(tags: [Tag("New.One", "DB5.DBW0")])),
            (Attempt, Proposed(tags: [Tag("A.Bad", "D100")])),
            (Password, Proposed(tags: [Tag("New.One", "DB5.DBW0")])),
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
        var proposed = Proposed(tags: [Tag("New.One", "DB5.DBW0")]);
        for (var i = 0; i < 4; i++)
        {
            using var wrong = await PutAsync(Body(await RevisionAsync(), "wrong-" + i, proposed));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var restarter = Restarter();
        Assert.True(restarter.TryRequest(Encoding.UTF8.GetBytes(
            """{"SchemaVersion":1,"Edge":{"Plc":{"Host":"127.0.0.1"},"Tags":[{"TagId":"B.Two","Address":"DB1.DBW2","DataType":"Int"}]}}"""), out var applyId));
        restarter.Release(applyId);
        Assert.Equal("applied", (await WaitForApplyAsync(applyId)).GetProperty("state").GetString());

        var revision = await RevisionAsync();
        using (var fifth = await PutAsync(Body(revision, "wrong-4", proposed)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, fifth.StatusCode);
        }

        using var locked = await PutAsync(Body(revision, Password, proposed));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    private static readonly Dictionary<string, string> NoHeaders = [];

    private async Task StartAsync(bool withPassword = true, bool withWritable = false, bool commandsEnabled = false)
    {
        if (withPassword)
        {
            SettingsPassword.Store(Path.Combine(_contentRoot, "data", "edge.settings-password.json"), Password);
        }

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge:EdgeId"] = "settings-api-test",
            ["Edge:Plc:Host"] = "127.0.0.1",
            ["Edge:Plc:Port"] = _plcPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Edge:Plc:TimeoutMs"] = "500",
            ["Edge:Tags:0:TagId"] = "Line1.Speed",
            ["Edge:Tags:0:Address"] = "DB1.DBW2",
            ["Edge:Tags:0:DataType"] = "Int",
            ["Edge:Buffer:Path"] = "data/edge.db",
            ["Edge:Poll:IntervalMs"] = "1000",
            ["Edge:Mqtt:Host"] = "127.0.0.1",
            ["Edge:Mqtt:Port"] = ClosedPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Edge:Mqtt:Username"] = "settings-api-test",
            ["Edge:Mqtt:Password"] = "test-only-password",
            ["Edge:Commands:Enabled"] = commandsEnabled ? "true" : "false",
            ["Edge:Monitor:Enabled"] = "true",
            ["Edge:Monitor:Port"] = _port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (withWritable)
        {
            // Line1.Setpoint writable 0..1500, as an administrator would configure it.
            settings["Edge:Tags:0:TagId"] = "Line1.Setpoint";
            settings["Edge:Tags:0:Address"] = "DB1.DBW4";
            settings["Edge:Tags:0:Writable"] = "true";
            settings["Edge:Tags:0:Min"] = "0";
            settings["Edge:Tags:0:Max"] = "1500";
            settings["Edge:Tags:1:TagId"] = "Line1.Speed";
            settings["Edge:Tags:1:Address"] = "DB1.DBW2";
            settings["Edge:Tags:1:DataType"] = "Int";
        }

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _contentRoot });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddEdgeSupervisor();
        _outer = builder.Build();
        await _outer.StartAsync();
    }

    private EdgeRestarter Restarter() => _outer!.Services.GetRequiredService<EdgeRestarter>();

    private async Task<string> SaveAndWaitAsync(JsonObject proposed)
    {
        using var response = await PutAsync(Body(await RevisionAsync(), Password, proposed));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var applyId = (await ReadJsonAsync(response)).GetProperty("applyId").GetString()!;
        Assert.Equal("applied", (await WaitForApplyAsync(applyId)).GetProperty("state").GetString());
        return applyId;
    }

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

    private static JsonObject WithPlcKey(JsonObject settings, string key, JsonNode value)
    {
        settings["Plc"]![key] = value;
        return settings;
    }

    private static JsonObject WithKey(JsonObject settings, string key, JsonNode value)
    {
        settings[key] = value;
        return settings;
    }

    /// <summary>Proposed settings; the PLC defaults to the one running (same host, port and timeout).</summary>
    private JsonObject Proposed(
        JsonObject[] tags, string protocol = "S7", string host = "127.0.0.1", int? port = null, int timeoutMs = 500, int intervalMs = 1000)
    {
        var plc = new JsonObject { ["Protocol"] = protocol, ["Host"] = host, ["TimeoutMs"] = timeoutMs, ["Port"] = port ?? _plcPort };

        return new JsonObject
        {
            ["Plc"] = plc,
            ["Poll"] = new JsonObject { ["IntervalMs"] = intervalMs },
            ["Tags"] = new JsonArray([.. tags]),
        };
    }

    private static JsonObject Tag(string tagId, string address, string dataType = "Int", bool writable = false) => new()
    {
        ["TagId"] = tagId,
        ["Address"] = address,
        ["DataType"] = dataType,
        ["Writable"] = writable,
    };

    /// <summary>The configured writable tag, sent back as the page would (Writable=false), optionally altered.</summary>
    private static JsonObject WritableTag(
        string tagId = "Line1.Setpoint", string address = "DB1.DBW4", string dataType = "Int", decimal min = 0, decimal max = 1500) => new()
    {
        ["TagId"] = tagId,
        ["Address"] = address,
        ["DataType"] = dataType,
        ["Writable"] = false,
        ["Min"] = min,
        ["Max"] = max,
    };

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
