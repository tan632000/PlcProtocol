using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

public class MonitorTests
{
    private static readonly (string TagId, string Address, string Type)[] ThreeTags =
    [
        ("Line1.Running", "DB1.DBX0.0", "Bool"),
        ("Line1.Speed", "DB1.DBW2", "Int"),
        ("Line1.Temperature", "DB1.DBD6", "Real"),
    ];

    [Fact]
    public async Task Status_ShowsLastPollCycle()
    {
        var driver = new FakePlcDriver();
        driver.SetRead("DB1.DBX0.0", true);
        driver.SetRead("DB1.DBW2", (short)250);
        driver.SetRead("DB1.DBD6", 25.5f);
        var port = FreePort();
        await using var edge = await StartAsync(driver, port);
        using var http = Client(port);

        var status = await WaitForStatusAsync(http, s => s.GetProperty("lastCycleUtc").ValueKind != JsonValueKind.Null);

        Assert.Equal("test-edge", status.GetProperty("edgeId").GetString());
        Assert.Equal("ok", status.GetProperty("plc").GetProperty("state").GetString());
        Assert.False(status.GetProperty("mqtt").GetProperty("connected").GetBoolean()); // no broker in this test
        var tags = status.GetProperty("tags").EnumerateArray().ToList();
        Assert.Equal(["Line1.Running", "Line1.Speed", "Line1.Temperature"], tags.Select(t => t.GetProperty("tagId").GetString()));
        Assert.Equal(["DB1.DBX0.0", "DB1.DBW2", "DB1.DBD6"], tags.Select(t => t.GetProperty("address").GetString()));
        Assert.Equal(["true", "250", "25.5"], tags.Select(t => t.GetProperty("value").GetRawText()));
        Assert.All(tags, t => Assert.Equal("good", t.GetProperty("quality").GetString()));
        Assert.All(tags, t => Assert.Equal(JsonValueKind.Null, t.GetProperty("error").ValueKind));
    }

    [Fact]
    public async Task Status_ExplainsUnreachablePlc()
    {
        var driver = new FakePlcDriver();
        driver.SetReadFailure("DB1.DBX0.0", new PlcUnavailableException("connect timed out"));
        var port = FreePort();
        await using var edge = await StartAsync(driver, port);
        using var http = Client(port);

        var status = await WaitForStatusAsync(http, s => s.GetProperty("lastCycleUtc").ValueKind != JsonValueKind.Null);

        Assert.Equal("unavailable", status.GetProperty("plc").GetProperty("state").GetString());
        var tags = status.GetProperty("tags").EnumerateArray().ToList();
        Assert.All(tags, t => Assert.Equal("bad", t.GetProperty("quality").GetString()));
        Assert.All(tags, t => Assert.Equal(JsonValueKind.Null, t.GetProperty("value").ValueKind));
        Assert.Equal("connect timed out", tags[0].GetProperty("error").GetString());
        Assert.Equal("Not read: connect timed out", tags[1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task Status_S7ShowsProtocolPortRackAndSlot()
    {
        var port = FreePort();
        await using var edge = await StartAsync(new FakePlcDriver(), port, plcOverrides: new Dictionary<string, string?>
        {
            ["Edge:Plc:Port"] = "102",
            ["Edge:Plc:Rack"] = "0",
            ["Edge:Plc:Slot"] = "1",
        });
        using var http = Client(port);

        var status = await WaitForStatusAsync(http, _ => true);

        var plc = status.GetProperty("plc");
        Assert.Equal("S7", plc.GetProperty("protocol").GetString());
        Assert.Equal(102, plc.GetProperty("port").GetInt32());
        Assert.Equal(0, plc.GetProperty("rack").GetInt32());
        Assert.Equal(1, plc.GetProperty("slot").GetInt32());
    }

    [Fact]
    public async Task Status_SlmpShowsProtocolAndPortWithoutRackAndSlot()
    {
        var port = FreePort();
        await using var edge = await StartAsync(new FakePlcDriver(), port, plcOverrides: new Dictionary<string, string?>
        {
            ["Edge:Plc:Protocol"] = "Slmp",
            ["Edge:Plc:Port"] = "5000",
            ["Edge:Plc:TimeoutMs"] = "1000",
            ["Edge:Commands:Enabled"] = "false",
            ["Edge:Tags:0:Address"] = "D0",
            ["Edge:Tags:1:Address"] = "D1",
            ["Edge:Tags:2:Address"] = "D2",
            ["Edge:Tags:0:DataType"] = "Word",
            ["Edge:Tags:1:DataType"] = "Word",
            ["Edge:Tags:2:DataType"] = "Word",
        });
        using var http = Client(port);

        var status = await WaitForStatusAsync(http, _ => true);

        var plc = status.GetProperty("plc");
        Assert.Equal("Slmp", plc.GetProperty("protocol").GetString());
        Assert.Equal(5000, plc.GetProperty("port").GetInt32());
        Assert.Equal(JsonValueKind.Null, plc.GetProperty("rack").ValueKind);
        Assert.Equal(JsonValueKind.Null, plc.GetProperty("slot").ValueKind);
    }

    [Fact]
    public async Task Page_IsServed()
    {
        var port = FreePort();
        await using var edge = await StartAsync(new FakePlcDriver(), port);
        using var http = Client(port);

        using var response = await http.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("PLC Monitor", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task ForeignHostHeader_IsRejected()
    {
        // A page on another site that rebinds its DNS name to 127.0.0.1 still sends its own Host header.
        var port = FreePort();
        await using var edge = await StartAsync(new FakePlcDriver(), port);
        using var http = Client(port);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Host = "evil.example";

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_DoesNotListen()
    {
        var port = FreePort();
        await using var edge = await StartAsync(new FakePlcDriver(), port, enabled: false);
        using var http = Client(port);

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("/api/status"));
    }

    [Fact]
    public async Task PortInUse_EdgeKeepsRunning()
    {
        var port = FreePort();
        using var squatter = new TcpListener(IPAddress.Loopback, port);
        squatter.Start();
        var driver = new FakePlcDriver();

        // StartAsync must not throw: the page is optional, polling is not.
        await using var edge = await StartAsync(driver, port);

        await WaitUntilAsync(() => driver.Reads.Count > 0, "poller still reads the PLC");
    }

    [Fact]
    public async Task InvalidPort_FailsAtStart()
    {
        await using var edge = EdgeTestHost.Create(new Dictionary<string, string?>
        {
            ["Edge:Monitor:Enabled"] = "true",
            ["Edge:Monitor:Port"] = "0",
        });

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => edge.Host.StartAsync());
        Assert.Contains("Edge:Monitor:Port", ex.Message, StringComparison.Ordinal);
    }

    private static async Task<EdgeTestHost> StartAsync(FakePlcDriver driver, int port, bool enabled = true, Dictionary<string, string?>? plcOverrides = null)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Edge:Monitor:Enabled"] = enabled ? "true" : "false",
            ["Edge:Monitor:Port"] = port.ToString(CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < ThreeTags.Length; i++)
        {
            overrides[$"Edge:Tags:{i}:TagId"] = ThreeTags[i].TagId;
            overrides[$"Edge:Tags:{i}:Address"] = ThreeTags[i].Address;
            overrides[$"Edge:Tags:{i}:DataType"] = ThreeTags[i].Type;
        }

        foreach (var (key, value) in plcOverrides ?? [])
        {
            overrides[key] = value;
        }

        var edge = EdgeTestHost.Create(overrides, services => services.AddSingleton<IPlcDriver>(driver), runAgentServices: true);
        await edge.Host.StartAsync();
        return edge;
    }

    private static HttpClient Client(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static async Task<JsonElement> WaitForStatusAsync(HttpClient http, Func<JsonElement, bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            using var document = JsonDocument.Parse(await http.GetStringAsync("/api/status"));
            var status = document.RootElement.Clone();
            if (condition(status))
            {
                return status;
            }

            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), "timed out waiting for the monitor status");
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), $"timed out waiting for: {what}");
            await Task.Delay(10);
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
