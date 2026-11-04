using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.EdgeDesktop.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialIot.Edge.Tests.Desktop;

/// <summary>
/// The desktop app's models against the real <c>/api/status</c> JSON. The app reads leniently; these tests read
/// strictly, so a property the Edge renames or removes fails here instead of showing blanks to an operator.
/// </summary>
public class StatusContractTests
{
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    [Fact]
    public async Task SinglePlcStatus_ReadsStrictly()
    {
        var json = await SinglePlcStatusJsonAsync();

        var status = JsonSerializer.Deserialize<EdgeStatus>(json, Strict)!;

        Assert.Equal("test-edge", status.EdgeId);
        Assert.NotNull(status.Plc);
        Assert.NotNull(status.LastCycleUtc);
        Assert.NotNull(status.PollIntervalMs);
        Assert.Equal(["Line1.Running", "Line1.Speed"], status.Tags.Select(t => t.TagId));
        Assert.Equal("true", status.Tags[0].Value!.Value.GetRawText());
        Assert.Equal("good", status.Tags[0].Quality);
        Assert.Single(status.Devices);
        Assert.NotNull(status.SettingsApply);
    }

    [Fact]
    public async Task MultiPlcStatus_ReadsStrictly()
    {
        var json = await MultiPlcStatusJsonAsync();

        var status = JsonSerializer.Deserialize<EdgeStatus>(json, Strict)!;

        Assert.Null(status.Plc);
        Assert.Null(status.PollIntervalMs);
        Assert.Equal(["A", "Spare"], status.Devices.Select(d => d.Name));
        Assert.Equal(["connected", "disabled"], status.Devices.Select(d => d.State));
        Assert.Equal(["A.Speed"], status.Tags.Select(t => t.TagId));
    }

    [Theory]
    [InlineData("edgeId")]
    [InlineData("lastCycleUtc")]
    [InlineData("devices[0].lastGoodUtc")]
    [InlineData("tags[0].timestampUtc")]
    [InlineData("plc.timeoutMs")]
    [InlineData("settingsApply.state")]
    public async Task RemovedProperty_FailsTheStrictRead(string path)
    {
        var node = JsonNode.Parse(await SinglePlcStatusJsonAsync())!;
        var (parent, name) = Locate(node, path);

        parent.Remove(name);

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<EdgeStatus>(node.ToJsonString(), Strict));
    }

    [Theory]
    [InlineData("edgeId")]
    [InlineData("bufferPending")]
    [InlineData("devices[0].lastGoodUtc")]
    [InlineData("tags[0].writable")]
    [InlineData("mqtt.connected")]
    public async Task RenamedProperty_FailsTheStrictRead(string path)
    {
        var node = JsonNode.Parse(await SinglePlcStatusJsonAsync())!;
        var (parent, name) = Locate(node, path);
        var value = parent[name];

        parent.Remove(name);
        parent[name + "Renamed"] = value;

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<EdgeStatus>(node.ToJsonString(), Strict));
    }

    [Fact]
    public async Task AppRead_ToleratesAnUnknownProperty()
    {
        var node = JsonNode.Parse(await SinglePlcStatusJsonAsync())!;
        node["addedByANewerEdge"] = 1;

        var status = StatusClient.Parse(node.ToJsonString());

        Assert.Equal("test-edge", status!.EdgeId);
    }

    private static (JsonObject Parent, string Name) Locate(JsonNode root, string path)
    {
        var parts = path.Split('.');
        var node = root;
        foreach (var part in parts[..^1])
        {
            node = part.EndsWith(']')
                ? node[part[..part.IndexOf('[', StringComparison.Ordinal)]]![int.Parse(part[(part.IndexOf('[', StringComparison.Ordinal) + 1)..^1], CultureInfo.InvariantCulture)]!
                : node[part]!;
        }

        return (node.AsObject(), parts[^1]);
    }

    private static async Task<string> SinglePlcStatusJsonAsync()
    {
        var driver = new FakePlcDriver();
        driver.SetRead("DB1.DBX0.0", true);
        driver.SetRead("DB1.DBW2", (short)250);
        var port = FreePort();
        await using var edge = EdgeTestHost.Create(
            new Dictionary<string, string?>
            {
                ["Edge:Monitor:Enabled"] = "true",
                ["Edge:Monitor:Port"] = port.ToString(CultureInfo.InvariantCulture),
                ["Edge:Tags:0:TagId"] = "Line1.Running",
                ["Edge:Tags:0:Address"] = "DB1.DBX0.0",
                ["Edge:Tags:0:DataType"] = "Bool",
                ["Edge:Tags:1:TagId"] = "Line1.Speed",
                ["Edge:Tags:1:Address"] = "DB1.DBW2",
                ["Edge:Tags:1:DataType"] = "Int",
            },
            services => services.UseDriver(driver),
            runAgentServices: true);
        await edge.Host.StartAsync();
        return await WaitForStatusJsonAsync(port, s => s.GetProperty("lastCycleUtc").ValueKind != JsonValueKind.Null);
    }

    private static async Task<string> MultiPlcStatusJsonAsync()
    {
        var driver = new FakePlcDriver();
        driver.SetRead("DB1.DBW2", (short)7);
        var port = FreePort();
        var settings = EdgeTestHost.MultiDevice(new Dictionary<string, string?>
        {
            ["Edge:Channels:0:Name"] = "S7",
            ["Edge:Channels:0:Driver"] = "S7",
            ["Edge:Channels:0:Devices:0:Name"] = "A",
            ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
            ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1",
            ["Edge:Channels:0:Devices:0:Tags:0:Name"] = "Speed",
            ["Edge:Channels:0:Devices:0:Tags:0:Address"] = "DB1.DBW2",
            ["Edge:Channels:0:Devices:0:Tags:0:DataType"] = "Int",
            ["Edge:Channels:0:Devices:1:Name"] = "Spare",
            ["Edge:Channels:0:Devices:1:Model"] = "S7-1200",
            ["Edge:Channels:0:Devices:1:Host"] = "127.0.0.2",
            ["Edge:Channels:0:Devices:1:Enabled"] = "false",
            ["Edge:Monitor:Enabled"] = "true",
            ["Edge:Monitor:Port"] = port.ToString(CultureInfo.InvariantCulture),
        });
        await using var edge = EdgeTestHost.Create(settings, services => services.UseDriver(driver), runAgentServices: true);
        await edge.Host.StartAsync();
        return await WaitForStatusJsonAsync(port, s => s.GetProperty("devices")[0].GetProperty("state").GetString() == "connected");
    }

    private static async Task<string> WaitForStatusJsonAsync(int port, Func<JsonElement, bool> condition)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var json = await http.GetStringAsync("/api/status");
            using (var document = JsonDocument.Parse(json))
            {
                if (condition(document.RootElement))
                {
                    return json;
                }
            }

            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), "timed out waiting for the monitor status");
            await Task.Delay(20);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
