using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Cloud.Tests.Infrastructure;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Cloud.Tests;

/// <summary>
/// <c>GET /api/v1/edges/{edgeId}/devices</c> and <c>.../devices/{device}/tags</c> for Viewers, with staleness on the
/// Cloud clock, latest values joined in, <c>canWrite</c> honouring the Cloud's own command switch, and
/// <c>devicesUpdated</c> pushed to the Edge's SignalR group after a stored round.
/// </summary>
[Collection(CloudCollection.Name)]
public class DevicesApiTests(PostgresFixture database)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Devices_ReturnsTheStoredRound()
    {
        await using var ctx = await StartAsync(database);
        await ctx.StoreRoundAsync();

        var body = await ctx.GetJsonAsync("/api/v1/edges/edge-a/devices");

        Assert.Equal("edge-a", body.GetProperty("edgeId").GetString());
        Assert.Equal(Start.UtcDateTime, body.GetProperty("reportedUtc").GetDateTime());
        Assert.False(body.GetProperty("stale").GetBoolean());
        Assert.True(body.GetProperty("commandsEnabled").GetBoolean());
        var devices = body.GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal(["Line", "Press"], devices.Select(d => d.GetProperty("name").GetString()));
        var line = devices[0];
        Assert.Equal(("S7-1200", true, true, "connected", "tagErrors", 2),
            (line.GetProperty("model").GetString(), line.GetProperty("enabled").GetBoolean(), line.GetProperty("writesEnabled").GetBoolean(),
             line.GetProperty("state").GetString(), line.GetProperty("lastError").GetString(), line.GetProperty("tagCount").GetInt32()));
        Assert.Equal(Start.AddSeconds(-1).UtcDateTime, line.GetProperty("lastGoodUtc").GetDateTime());
        Assert.Equal(("disabled", JsonValueKind.Null), (devices[1].GetProperty("state").GetString(), devices[1].GetProperty("lastError").ValueKind));
    }

    [Fact]
    public async Task Devices_TurnStaleAfter150SecondsOnTheCloudClock()
    {
        await using var ctx = await StartAsync(database);
        await ctx.StoreRoundAsync();

        ctx.Clock.Advance(TimeSpan.FromSeconds(150));
        var fresh = await ctx.GetJsonAsync("/api/v1/edges/edge-a/devices");
        Assert.False(fresh.GetProperty("stale").GetBoolean());

        ctx.Clock.Advance(TimeSpan.FromSeconds(1));
        var stale = await ctx.GetJsonAsync("/api/v1/edges/edge-a/devices");
        Assert.True(stale.GetProperty("stale").GetBoolean());
        Assert.All(stale.GetProperty("devices").EnumerateArray(), d => Assert.Equal("stale", d.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task AllowListedEdgeWithoutAnInventory_IsEmptyAndStale()
    {
        await using var ctx = await StartAsync(database);

        var body = await ctx.GetJsonAsync("/api/v1/edges/edge-b/devices");

        Assert.Equal(JsonValueKind.Null, body.GetProperty("reportedUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("commandsEnabled").ValueKind);
        Assert.True(body.GetProperty("stale").GetBoolean());
        Assert.Empty(body.GetProperty("devices").EnumerateArray());
    }

    [Fact]
    public async Task UnknownEdgeOrDevice_Is404_DeviceNameIgnoresCase()
    {
        await using var ctx = await StartAsync(database);
        await ctx.StoreRoundAsync();
        var token = ctx.Host.CreateToken(["Viewer"]);

        foreach (var path in new[] { "/api/v1/edges/edge-z/devices", "/api/v1/edges/edge-z/devices/Line/tags" })
        {
            using var response = await ctx.SendAsync(path, token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("edge_not_found", (await Json(response)).GetProperty("title").GetString());
        }

        foreach (var path in new[] { "/api/v1/edges/edge-a/devices/Ghost/tags", "/api/v1/edges/edge-a/devices/bad%20name/tags", "/api/v1/edges/edge-b/devices/Line/tags" })
        {
            using var response = await ctx.SendAsync(path, token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("device_not_found", (await Json(response)).GetProperty("title").GetString());
        }

        var tags = await ctx.GetJsonAsync("/api/v1/edges/edge-a/devices/LINE/tags");
        Assert.Equal(2, tags.GetArrayLength());
    }

    [Fact]
    public async Task Tags_InEdgeOrder_WithLatestValues()
    {
        await using var ctx = await StartAsync(database);
        await ctx.StoreRoundAsync();
        await ctx.PublishTelemetryAsync("Line.Speed", 1200);
        await ctx.WaitUntilAsync(async () => await ctx.QueryAsync(db => db.LatestValues.CountAsync()) == 1, "latest value stored");

        var tags = (await ctx.GetJsonAsync("/api/v1/edges/edge-a/devices/Line/tags")).EnumerateArray().ToList();

        Assert.Equal(["Line.Speed", "Line.Batch"], tags.Select(t => t.GetProperty("tagId").GetString()));
        var speed = tags[0];
        Assert.Equal(("Speed", "Int", true, 0m, 3000m, "Belt speed"),
            (speed.GetProperty("name").GetString(), speed.GetProperty("dataType").GetString(), speed.GetProperty("writable").GetBoolean(),
             speed.GetProperty("min").GetDecimal(), speed.GetProperty("max").GetDecimal(), speed.GetProperty("description").GetString()));
        Assert.Equal((1200, "good"), (speed.GetProperty("value").GetInt32(), speed.GetProperty("quality").GetString()));
        Assert.Equal(Start.UtcDateTime, speed.GetProperty("timestampUtc").GetDateTime());

        var batch = tags[1];
        Assert.Equal(20, batch.GetProperty("length").GetInt32());
        Assert.Equal(JsonValueKind.Null, batch.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, batch.GetProperty("quality").ValueKind);
        Assert.Equal(JsonValueKind.Null, batch.GetProperty("timestampUtc").ValueKind);
    }

    [Fact]
    public async Task CanWrite_NeedsEveryEdgeSwitch_AndTheCloudSwitch()
    {
        await using (var on = await StartAsync(database))
        {
            await on.StoreRoundAsync();
            var line = (await on.GetJsonAsync("/api/v1/edges/edge-a/devices/Line/tags")).EnumerateArray().ToList();
            Assert.Equal([true, false], line.Select(t => t.GetProperty("canWrite").GetBoolean())); // Batch is not Writable
            var press = (await on.GetJsonAsync("/api/v1/edges/edge-a/devices/Press/tags")).EnumerateArray().Single();
            Assert.True(press.GetProperty("writable").GetBoolean());
            Assert.False(press.GetProperty("canWrite").GetBoolean()); // the device is disabled
        }

        await using (var withoutEdgeWrites = await StartAsync(database))
        {
            await withoutEdgeWrites.StoreRoundAsync(edgeCommandsEnabled: false);
            Assert.False((await withoutEdgeWrites.GetJsonAsync("/api/v1/edges/edge-a/devices")).GetProperty("commandsEnabled").GetBoolean());
            Assert.All((await withoutEdgeWrites.GetJsonAsync("/api/v1/edges/edge-a/devices/Line/tags")).EnumerateArray(),
                t => Assert.False(t.GetProperty("canWrite").GetBoolean()));
        }

        await using var off = await StartAsync(database, cloudCommandsEnabled: false);
        await off.StoreRoundAsync();
        Assert.False((await off.GetJsonAsync("/api/v1/edges/edge-a/devices")).GetProperty("commandsEnabled").GetBoolean());
        Assert.All((await off.GetJsonAsync("/api/v1/edges/edge-a/devices/Line/tags")).EnumerateArray(),
            t => Assert.False(t.GetProperty("canWrite").GetBoolean()));
    }

    [Fact]
    public async Task Routes_NeedAViewerOrOperatorToken()
    {
        await using var ctx = await StartAsync(database);

        foreach (var path in new[] { "/api/v1/edges/edge-a/devices", "/api/v1/edges/edge-a/devices/Line/tags" })
        {
            using var anonymous = await ctx.SendAsync(path, token: null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var noRole = await ctx.SendAsync(path, ctx.Host.CreateToken([]));
            Assert.Equal(HttpStatusCode.Forbidden, noRole.StatusCode);
            using var asOperator = await ctx.SendAsync(path, ctx.Host.CreateToken(["Operator"]));
            Assert.NotEqual(HttpStatusCode.Forbidden, asOperator.StatusCode);
        }
    }

    [Fact]
    public async Task DevicesUpdated_IsPushedToTheEdgesGroupOnly_WithTheDevicesBody()
    {
        await using var ctx = await StartAsync(database);
        var edgeA = new ConcurrentQueue<JsonElement>();
        var edgeB = new ConcurrentQueue<JsonElement>();
        await using var hubA = await ctx.ConnectHubAsync("edge-a", edgeA);
        await using var hubB = await ctx.ConnectHubAsync("edge-b", edgeB);

        // A completion time finer than the database keeps: the push must still equal what GET reads back.
        ctx.Clock.Advance(TimeSpan.FromTicks(1_234_567));
        await ctx.StoreRoundAsync();
        await ctx.WaitUntilAsync(() => Task.FromResult(!edgeA.IsEmpty), "devicesUpdated for edge-a");
        await Task.Delay(300);

        var pushed = Assert.Single(edgeA);
        Assert.Empty(edgeB);
        var body = await ctx.GetJsonAsync("/api/v1/edges/edge-a/devices");
        Assert.Equal(body.GetRawText(), pushed.GetRawText());
    }

    [Fact]
    public async Task Responses_CarryNoNetworkDetailOrRawErrorText()
    {
        await using var ctx = await StartAsync(database);
        await ctx.StoreRoundAsync();
        string[] forbidden = ["host", "port", "rack", "slot", "address", "error"];

        foreach (var path in new[] { "/api/v1/edges/edge-a/devices", "/api/v1/edges/edge-a/devices/Line/tags" })
        {
            var names = Names(await ctx.GetJsonAsync(path)).ToList();
            Assert.NotEmpty(names);
            Assert.DoesNotContain(names, n => forbidden.Contains(n, StringComparer.OrdinalIgnoreCase));
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static IEnumerable<string> Names(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var name in Names(property.Value))
                {
                    yield return name;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in element.EnumerateArray().SelectMany(Names))
            {
                yield return name;
            }
        }
    }

    private static async Task<Context> StartAsync(PostgresFixture database, bool cloudCommandsEnabled = true)
    {
        var clock = new FakeTimeProvider(Start);
        var host = new CloudTestHost(
            database,
            new Dictionary<string, string?> { ["Cloud:Commands:Enabled"] = cloudCommandsEnabled ? "true" : "false" },
            services => services.AddSingleton<TimeProvider>(clock));
        var client = host.CreateClient();
        var connection = host.Services.GetRequiredService<CloudMqttConnection>();
        await connection.FirstAttemptCompleted.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(connection.IsConnected);
        return new Context(host, client, clock);
    }

    private sealed record Context(CloudTestHost Host, HttpClient Client, FakeTimeProvider Clock) : IAsyncDisposable
    {
        /// <summary>Publishes a complete round for edge-a and waits until it is stored.</summary>
        public async Task StoreRoundAsync(bool edgeCommandsEnabled = true)
        {
            var boot = Guid.NewGuid();
            var line = Device("Line", 2) with
            {
                WritesEnabled = true,
                LastGoodUtc = Start.AddSeconds(-1),
                LastError = InventoryDeviceError.TagErrors,
            };
            // Writes on but the device disabled: only the Enabled switch keeps its tag from canWrite.
            var press = Device("Press", 1) with { Model = "FX5U", Enabled = false, WritesEnabled = true, State = InventoryDeviceState.Disabled };
            string[] parts =
            [
                Part(boot, 0, 3, edgeCommandsEnabled, devices: [line, press]),
                Part(boot, 1, 3, edgeCommandsEnabled, tags: new InventoryTags
                {
                    Device = "Line",
                    Items =
                    [
                        Tag("Line.Speed") with { Name = "Speed", Writable = true, Min = 0, Max = 3000, Description = "Belt speed" },
                        Tag("Line.Batch") with { Name = "Batch", DataType = "String", Length = 20 },
                    ],
                }),
                Part(boot, 2, 3, edgeCommandsEnabled, tags: new InventoryTags { Device = "Press", Items = [Tag("Press.Count") with { Writable = true }] }),
            ];
            foreach (var part in parts)
            {
                await Host.Broker.PublishAsync("iiot/edge-a/inventory", part);
            }

            await WaitUntilAsync(async () => await QueryAsync(db => db.EdgeInventory.AnyAsync(r => r.EdgeId == "edge-a" && r.BootId == boot)), "round stored");
        }

        public Task PublishTelemetryAsync(string tagId, int value) => Host.Broker.PublishAsync(
            "iiot/edge-a/telemetry",
            ContractJson.Serialize(new TelemetryPayload
            {
                SchemaVersion = ContractJson.SchemaVersion,
                EdgeId = "edge-a",
                StreamId = Guid.NewGuid(),
                Sequence = 1,
                TagId = tagId,
                Value = JsonSerializer.SerializeToElement(value),
                Quality = TelemetryQuality.Good,
                TimestampUtc = Start,
            }));

        public async Task<HttpResponseMessage> SendAsync(string path, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await Client.SendAsync(request);
        }

        public async Task<JsonElement> GetJsonAsync(string path)
        {
            using var response = await SendAsync(path, Host.CreateToken(["Viewer"]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await Json(response);
        }

        public async Task<HubConnection> ConnectHubAsync(string edgeId, ConcurrentQueue<JsonElement> received)
        {
            var token = Host.CreateToken(["Viewer"]);
            var hub = new HubConnectionBuilder()
                .WithUrl(new Uri(Host.Server.BaseAddress, "hubs/live"), options =>
                {
                    options.HttpMessageHandlerFactory = _ => Host.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .Build();
            hub.On<JsonElement>("devicesUpdated", body => received.Enqueue(body));
            await hub.StartAsync();
            await hub.InvokeAsync("JoinEdge", edgeId);
            return hub;
        }

        public async Task<T> QueryAsync<T>(Func<CloudDbContext, Task<T>> query)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            return await query(scope.ServiceProvider.GetRequiredService<CloudDbContext>());
        }

        public async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
        {
            var deadline = Stopwatch.StartNew();
            while (!await condition())
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), $"timed out waiting for: {what}");
                await Task.Delay(50);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
        }

        private static string Part(Guid boot, int part, int partCount, bool commandsEnabled, IReadOnlyList<InventoryDevice>? devices = null, InventoryTags? tags = null) =>
            ContractJson.Serialize(new EdgeInventory
            {
                SchemaVersion = ContractJson.SchemaVersion,
                EdgeId = "edge-a",
                BootId = boot,
                Round = 0,
                Part = part,
                PartCount = partCount,
                SentUtc = Start,
                CommandsEnabled = commandsEnabled,
                Devices = devices,
                Tags = tags,
            });

        private static InventoryDevice Device(string name, int tagCount) => new()
        {
            Name = name,
            Model = "S7-1200",
            Enabled = true,
            WritesEnabled = false,
            State = InventoryDeviceState.Connected,
            LastGoodUtc = null,
            LastError = null,
            TagCount = tagCount,
        };

        private static InventoryTag Tag(string tagId) => new()
        {
            TagId = tagId,
            Name = tagId,
            DataType = "Int",
            Length = null,
            Writable = false,
            Min = null,
            Max = null,
            Description = null,
        };
    }
}
