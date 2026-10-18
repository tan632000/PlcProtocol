using System.Diagnostics;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Polling;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Edge.Tests;

public class TagPollerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private static readonly (string TagId, string Address, string Type)[] ThreeTags =
    [
        ("Motor.Running", "DB1.DBX0.0", "Bool"),
        ("Line1.Speed", "DB1.DBW2", "Int"),
        ("Tank.Level", "DB1.DBD4", "Real"),
    ];

    [Fact]
    public async Task Cycle_AppendsOneSamplePerTag()
    {
        var driver = new FakePlcDriver();
        driver.SetRead("DB1.DBX0.0", true);
        driver.SetRead("DB1.DBW2", (short)-2);
        driver.SetRead("DB1.DBD4", 1.5f);
        await using var edge = await StartPollerAsync(driver, new FakeTimeProvider(), ThreeTags);

        await WaitUntilAsync(() => Poller(edge).WaitsArmed >= 1, "first cycle");
        var samples = await StoredAsync(edge);

        Assert.Equal(["Motor.Running", "Line1.Speed", "Tank.Level"], samples.Select(s => s.TagId));
        Assert.Equal(["true", "-2", "1.5"], samples.Select(s => s.Value?.GetRawText()));
        Assert.All(samples, s => Assert.Equal(TelemetryQuality.Good, s.Quality));
    }

    [Fact]
    public async Task NonFinite_IsNullBad()
    {
        var driver = new FakePlcDriver();
        driver.SetRead("DB1.DBD4", float.NaN);
        driver.SetRead("DB1.DBD8", float.PositiveInfinity);
        driver.SetRead("DB1.DBD12", 2.25f);
        await using var edge = await StartPollerAsync(driver, new FakeTimeProvider(),
        [
            ("A", "DB1.DBD4", "Real"),
            ("B", "DB1.DBD8", "Real"),
            ("C", "DB1.DBD12", "Real"),
        ]);

        await WaitUntilAsync(() => Poller(edge).WaitsArmed >= 1, "first cycle");
        var samples = await StoredAsync(edge);

        Assert.Equal([TelemetryQuality.Bad, TelemetryQuality.Bad, TelemetryQuality.Good], samples.Select(s => s.Quality));
        Assert.Null(samples[0].Value);
        Assert.Null(samples[1].Value);
        Assert.Equal("2.25", samples[2].Value?.GetRawText());
    }

    [Fact]
    public async Task Unavailable_ShortCircuitsAndBacksOff()
    {
        var clock = new FakeTimeProvider();
        var driver = new FakePlcDriver();
        driver.SetReadFailure("DB1.DBX0.0", new PlcUnavailableException("unreachable"));
        await using var edge = await StartPollerAsync(driver, clock, ThreeTags);
        var poller = Poller(edge);

        await WaitUntilAsync(() => poller.WaitsArmed >= 1, "first cycle");
        var samples = await StoredAsync(edge);
        Assert.Equal(3, samples.Count);
        Assert.All(samples, s => Assert.Equal(TelemetryQuality.Bad, s.Quality));
        Assert.Single(driver.Reads); // tags 2 and 3 were not read

        // The next due cycle is skipped, leaving the driver free for one interval.
        clock.Advance(Interval);
        await Task.Delay(200);
        Assert.Single(driver.Reads);
        Assert.Equal(1, poller.WaitsArmed);

        clock.Advance(Interval);
        await WaitUntilAsync(() => poller.WaitsArmed >= 2, "cycle after the back-off");
        Assert.Equal(2, driver.Reads.Count);
    }

    [Fact]
    public async Task AccessDenied_OnlyThatTagBad()
    {
        var driver = new FakePlcDriver();
        driver.SetReadFailure("DB1.DBX0.0", new PlcAccessDeniedException("PUT/GET disabled"));
        driver.SetRead("DB1.DBW2", (short)7);
        driver.SetRead("DB1.DBD4", 0.5f);
        await using var edge = await StartPollerAsync(driver, new FakeTimeProvider(), ThreeTags);

        await WaitUntilAsync(() => Poller(edge).WaitsArmed >= 1, "first cycle");
        var samples = await StoredAsync(edge);

        Assert.Equal([TelemetryQuality.Bad, TelemetryQuality.Good, TelemetryQuality.Good], samples.Select(s => s.Quality));
        Assert.Equal("7", samples[1].Value?.GetRawText());
        Assert.Equal(3, driver.Reads.Count);
    }

    [Fact]
    public async Task Schedule_NoBurstAfterOverrun()
    {
        var clock = new FakeTimeProvider();
        var driver = new FakePlcDriver();
        await using (var edge = await StartPollerAsync(driver, clock, [("Line1.Speed", "DB1.DBW2", "Int")]))
        {
            var poller = Poller(edge);
            await WaitUntilAsync(() => poller.WaitsArmed >= 1, "first cycle");

            // Interval by interval: one cycle per step.
            for (var i = 2; i <= 4; i++)
            {
                clock.Advance(Interval);
                await WaitUntilAsync(() => poller.WaitsArmed >= i, $"cycle {i}");
            }

            Assert.Equal(4, driver.Reads.Count);
        }

        // A cycle that takes 2.5 intervals: the next one starts at 3.0, once.
        clock = new FakeTimeProvider();
        driver = new FakePlcDriver();
        driver.Block();
        await using (var edge = await StartPollerAsync(driver, clock, [("Line1.Speed", "DB1.DBW2", "Int")]))
        {
            var poller = Poller(edge);
            await driver.GateEntered.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(Interval * 2.5);
            driver.Release();
            await WaitUntilAsync(() => poller.WaitsArmed >= 1, "overrun cycle finished");

            clock.Advance(Interval * 0.4); // t = 2.9: not due yet, and no catch-up for 1.0 and 2.0
            await Task.Delay(200);
            Assert.Single(driver.Reads);

            clock.Advance(Interval * 0.1); // t = 3.0
            await WaitUntilAsync(() => poller.WaitsArmed >= 2, "cycle at 3.0");
            await Task.Delay(200);
            Assert.Equal(2, driver.Reads.Count);
        }
    }

    [Fact]
    public async Task RealDriver_OutageDoesNotStarveWrites()
    {
        await using var plc = FakeS7Server.Start(FakeS7Mode.BlackHole);
        await using var edge = EdgeTestHost.Create(
            new Dictionary<string, string?>
            {
                ["Edge:Plc:Port"] = plc.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Edge:Plc:TimeoutMs"] = "300",
                ["Edge:Poll:IntervalMs"] = "1000",
            },
            runAgentServices: true);
        var store = edge.Services.GetRequiredService<TelemetryStore>();
        var driver = edge.Services.GetRequiredService<IPlcDriver>();

        await edge.Host.StartAsync();
        await WaitUntilAsync(() => store.CountAsync(default).GetAwaiter().GetResult() >= 1, "first failed cycle");
        var afterFailure = Stopwatch.StartNew();
        var acceptsBefore = plc.AcceptCount;

        // The write gets the connection (and fails as unreachable), it is not starved into "busy".
        var error = await Record.ExceptionAsync(() => driver.WriteAsync(PlcAddressParser.Parse("DB1.DBW2"), PlcDataType.Int, (short)1, default));
        Assert.IsType<PlcUnavailableException>(error);

        // Without the back-off the poller would connect again ~0.7 s after the failure; with it, not before ~1.7 s.
        // Check at 1.0 s: until then only the write connected.
        var remaining = TimeSpan.FromMilliseconds(1000) - afterFailure.Elapsed;
        Assert.True(remaining > TimeSpan.Zero, $"write took too long ({afterFailure.ElapsedMilliseconds} ms)");
        await Task.Delay(remaining);
        Assert.Equal(acceptsBefore + 1, plc.AcceptCount);

        await edge.Host.StopAsync();
    }

    [Fact]
    public async Task InvalidPollConfig_FailsAtStart()
    {
        await using var edge = EdgeTestHost.Create(new Dictionary<string, string?> { ["Edge:Poll:IntervalMs"] = "50" });

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => edge.Host.StartAsync());
        Assert.Contains("Edge:Poll:IntervalMs", ex.Message, StringComparison.Ordinal);
    }

    private static async Task<EdgeTestHost> StartPollerAsync(
        FakePlcDriver driver, FakeTimeProvider clock, (string TagId, string Address, string Type)[] tags)
    {
        var overrides = new Dictionary<string, string?>();
        for (var i = 0; i < tags.Length; i++)
        {
            overrides[$"Edge:Tags:{i}:TagId"] = tags[i].TagId;
            overrides[$"Edge:Tags:{i}:Address"] = tags[i].Address;
            overrides[$"Edge:Tags:{i}:DataType"] = tags[i].Type;
        }

        var edge = EdgeTestHost.Create(
            overrides,
            services =>
            {
                services.AddSingleton<IPlcDriver>(driver);
                services.AddSingleton<TimeProvider>(clock);
            },
            runAgentServices: true);
        await edge.Host.StartAsync();
        return edge;
    }

    private static TagPoller Poller(EdgeTestHost edge) => edge.HostedService<TagPoller>();

    private static async Task<List<TelemetryPayload>> StoredAsync(EdgeTestHost edge)
    {
        var rows = await edge.Services.GetRequiredService<TelemetryStore>().ReadBatchAsync(100, default);
        return rows.Select(r => ContractJson.Deserialize<TelemetryPayload>(r.Payload)).ToList();
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
}
