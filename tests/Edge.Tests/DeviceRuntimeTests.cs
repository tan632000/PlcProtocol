using System.Diagnostics;
using System.Text;
using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Monitoring;
using IndustrialIot.Edge.Polling;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// Each device has its own driver and its own read loop: devices read at their own intervals, and one PLC that
/// hangs does not slow another. Single-driver consumers get one device's driver, or a refusing one when there are
/// several.
/// </summary>
public sealed class DeviceRuntimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TwoDevices_ReadAtTheirOwnIntervals()
    {
        var clock = new FakeTimeProvider(Start);
        var drivers = new Dictionary<string, CountingDriver> { ["A"] = new(clock), ["B"] = new(clock) };
        await using var edge = await StartAsync(TwoDevices(intervalA: 200, intervalB: 1000), drivers, clock);
        var poller = edge.HostedService<TagPoller>();

        for (var t = 0; t <= 1000; t += 100)
        {
            if (t > 0)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            int a = (t / 200) + 1, b = (t / 1000) + 1;
            await WaitUntilAsync(
                () => drivers["A"].Reads == a && drivers["B"].Reads == b && poller.WaitsArmed == a + b,
                $"A={a} and B={b} reads at {t} ms (A={drivers["A"].Reads}, B={drivers["B"].Reads})");
        }

        Assert.Equal(6, drivers["A"].Reads);
        Assert.Equal(2, drivers["B"].Reads);
    }

    [Fact]
    public async Task ADeviceThatHangs_DoesNotDelayTheOther()
    {
        var clock = new FakeTimeProvider(Start);
        var drivers = new Dictionary<string, CountingDriver> { ["A"] = new(clock), ["B"] = new(clock) };
        drivers["A"].Hang();
        await using var edge = await StartAsync(TwoDevices(intervalA: 1000, intervalB: 200), drivers, clock);
        var poller = edge.HostedService<TagPoller>();

        // Device A never answers for 3 s; B keeps its 200 ms cycle.
        for (var t = 0; t <= 3000; t += 100)
        {
            if (t > 0)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            var b = (t / 200) + 1;
            await WaitUntilAsync(() => drivers["B"].Reads == b && poller.WaitsArmed == b, $"B={b} reads at {t} ms");
        }

        var times = drivers["B"].ReadTimes;
        var maxGap = times.Zip(times.Skip(1), (x, y) => y - x).Max();
        Assert.True(maxGap <= TimeSpan.FromMilliseconds(300), $"B's longest gap was {maxGap.TotalMilliseconds} ms");
        Assert.Equal(1, drivers["A"].Reads);
        drivers["A"].Release();
    }

    [Fact]
    public async Task ADisabledDevice_GetsNoDriverAndIsNotRead()
    {
        var clock = new FakeTimeProvider(Start);
        var drivers = new Dictionary<string, CountingDriver> { ["A"] = new(clock), ["B"] = new(clock) };
        var settings = TwoDevices(intervalA: 1000, intervalB: 1000);
        settings["Edge:Channels:0:Devices:1:Enabled"] = "false";
        var factory = new FakeDriverFactory(device => drivers[device.Name]);
        await using var edge = await StartAsync(settings, factory, clock);
        var poller = edge.HostedService<TagPoller>();

        await WaitUntilAsync(() => drivers["A"].Reads == 1 && poller.WaitsArmed == 1, "A's first read");

        Assert.Equal(["A"], factory.Created);
        Assert.Equal(0, drivers["B"].Reads);
        var b = edge.Services.GetRequiredService<PlcDeviceRegistry>().Devices.Single(d => d.Name == "B");
        Assert.Null(b.Driver);
        Assert.Equal(["B.Speed"], b.Catalog.Tags.Select(t => t.TagId));
    }

    [Fact]
    public async Task EachCycle_IsPublishedPerDevice_WithItsReadTimeAndRequestCount()
    {
        var clock = new FakeTimeProvider(Start);
        var drivers = new Dictionary<string, CountingDriver>
        {
            ["A"] = new(clock) { ReadTime = TimeSpan.FromMilliseconds(40), Requests = 3 },
            ["B"] = new(clock) { ReadTime = TimeSpan.FromMilliseconds(70), Requests = 1 },
        };
        await using var edge = await StartAsync(TwoDevices(intervalA: 1000, intervalB: 1000), drivers, clock);
        var poller = edge.HostedService<TagPoller>();

        await WaitUntilAsync(() => poller.WaitsArmed == 2, "both first cycles");

        var cycles = edge.Services.GetRequiredService<LiveTagSnapshot>().Devices;
        Assert.Equal(3, cycles["A"].Requests);
        Assert.Equal(40, cycles["A"].ReadDuration.TotalMilliseconds);
        Assert.Equal(1, cycles["B"].Requests);
        Assert.Equal(70, cycles["B"].ReadDuration.TotalMilliseconds);
        Assert.Equal(["A.Speed"], cycles["A"].Readings.Select(r => r.TagId));
        Assert.Equal(["B.Speed"], cycles["B"].Readings.Select(r => r.TagId));
    }

    [Fact]
    public async Task TheRealFactory_GivesEachDeviceADriverOfItsOwn()
    {
        var factory = new PlcDriverFactory(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var s7 = new ResolvedDevice("S7", PlcDriverKind.S7, "A", "S7-1200", "127.0.0.1", null, null, null, null, 1000, 1000, true, false, false, []);
        var fx = s7 with { ChannelName = "Fx", Driver = PlcDriverKind.Slmp3E, Name = "Press", Model = "FX5U", Port = 5000 };

        var first = factory.Create(s7);
        var second = factory.Create(s7 with { Name = "B", Host = "127.0.0.2" });
        var press = factory.Create(fx);

        Assert.IsType<SiemensS7Driver>(first);
        Assert.NotSame(first, second);
        Assert.IsType<Drivers.Slmp.MitsubishiSlmpDriver>(press);
        foreach (var driver in new[] { first, second, press })
        {
            await ((IAsyncDisposable)driver).DisposeAsync();
        }
    }

    [Fact]
    public void Snapshot_KeepsTheLatestCycleOfEveryDevice()
    {
        var snapshot = new LiveTagSnapshot();

        snapshot.Publish("A", Cycle("A.Speed", 10));
        snapshot.Publish("B", Cycle("B.Speed", 20));
        snapshot.Publish("A", Cycle("A.Speed", 30));

        Assert.Equal(30, snapshot.Devices["A"].ReadDuration.TotalMilliseconds);
        Assert.Equal(20, snapshot.Devices["B"].ReadDuration.TotalMilliseconds);
        Assert.Equal(["A.Speed", "B.Speed"], snapshot.Latest!.Readings.Select(r => r.TagId).Order());
    }

    [Fact]
    public async Task HostWithAnS7AndAnFx5u_Starts_WithEachDevicesOwnCatalogAndNoSingleDriverService()
    {
        var a = new FakePlcDriver();
        var press = new FakePlcDriver();
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
            ["Edge:Channels:0:Devices:0:Tags:0:Writable"] = "true",
            ["Edge:Channels:1:Name"] = "Fx",
            ["Edge:Channels:1:Driver"] = "Slmp3E",
            ["Edge:Channels:1:Devices:0:Name"] = "Press",
            ["Edge:Channels:1:Devices:0:Model"] = "FX5U",
            ["Edge:Channels:1:Devices:0:Host"] = "127.0.0.2",
            ["Edge:Channels:1:Devices:0:Port"] = "5000",
            ["Edge:Channels:1:Devices:0:Tags:0:Name"] = "D100",
            ["Edge:Channels:1:Devices:0:Tags:0:Address"] = "D100",
            ["Edge:Channels:1:Devices:0:Tags:0:DataType"] = "Int",
        });
        await using var edge = EdgeTestHost.Create(
            settings, services => services.AddSingleton<IPlcDriverFactory>(new FakeDriverFactory(d => d.Name == "A" ? a : press)));

        await edge.Host.StartAsync();

        var registry = edge.Services.GetRequiredService<PlcDeviceRegistry>();
        Assert.Equal(["A.Speed", "Press.D100"], registry.Devices.SelectMany(d => d.Catalog.Tags).Select(t => t.TagId));
        Assert.Equal([a, press], registry.Devices.Select(d => d.Driver));
        Assert.Null(edge.Services.GetService<IPlcDriver>());
        Assert.Null(edge.Services.GetService<PlcTagCatalog>());
        await edge.Host.StopAsync();
    }

    [Fact]
    public async Task Schema2File_StartsTheHost_WhileAppsettingsAndTheEnvironmentStillCarryEdgePlc()
    {
        var contentRoot = Directory.CreateTempSubdirectory("edge-multi-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(contentRoot, "data"));
            await File.WriteAllTextAsync(Path.Combine(contentRoot, "data", "edge.settings.json"), """
                {"SchemaVersion":2,"Edge":{"Channels":[{"Name":"S7","Driver":"S7","Devices":[
                  {"Name":"Line1","Model":"S7-1200","Host":"127.0.0.1",
                   "Tags":[{"Name":"Temp","Address":"DB1.DBD0","DataType":"Real"}]}]}]}}
                """, new UTF8Encoding(false));
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = contentRoot });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Edge:EdgeId"] = "multi-test",
                ["Edge:Plc:Protocol"] = "S7",
                ["Edge:Tags:0:TagId"] = "Line1.Running",
                ["Edge:Tags:0:Address"] = "DB1.DBX0.0",
                ["Edge:Tags:0:DataType"] = "Bool",
                ["Edge:Buffer:Path"] = "data/edge.db",
                ["Edge:Mqtt:Host"] = "no-broker.invalid",
                ["Edge:Mqtt:Username"] = "multi-test",
                ["Edge:Mqtt:Password"] = "not-a-secret-test",
                ["Edge:Monitor:Enabled"] = "false",
            });
            // Stands in for the installer's Edge__Plc__Host in the service environment.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Edge:Plc:Host"] = "192.168.3.20" });
            builder.Services.AddEdgeAgent(builder.Configuration);
            builder.Services.AddSingleton<IPlcDriverFactory>(new FakeDriverFactory(_ => new FakePlcDriver()));
            using var host = builder.Build();

            await host.StartAsync();

            var device = Assert.Single(host.Services.GetRequiredService<PlcDeviceRegistry>().Devices);
            Assert.Equal(("Line1", "127.0.0.1"), (device.Name, device.Device.Host));
            Assert.Equal(["Line1.Temp"], device.Catalog.Tags.Select(t => t.TagId));
            await host.StopAsync();
        }
        finally
        {
            EdgeTestHost.DeleteDirectory(contentRoot);
        }
    }

    [Fact]
    public async Task DriversAreDisposed_WhenTheHostStops()
    {
        var clock = new FakeTimeProvider(Start);
        var drivers = new Dictionary<string, CountingDriver> { ["A"] = new(clock), ["B"] = new(clock) };
        var edge = await StartAsync(TwoDevices(intervalA: 1000, intervalB: 1000), drivers, clock);

        await edge.Host.StopAsync();
        await edge.DisposeAsync();

        Assert.True(drivers["A"].Disposed);
        Assert.True(drivers["B"].Disposed);
    }

    [Theory]
    [InlineData("Edge:Channels:0:Devices:0:Port", "70000", "Device 'A': Port must be within 1..65535")]
    [InlineData("Edge:Channels:0:Devices:0:PollIntervalMs", "50", "Device 'A': PollIntervalMs must be within 100..3600000")]
    [InlineData("Edge:Channels:0:Devices:0:Tags:0:Address", "NotAnAddress", "Device 'A'")]
    public void InvalidDevice_FailsTheStartNamingTheDevice(string key, string value, string expected)
    {
        var settings = TwoDevices(intervalA: 1000, intervalB: 1000);
        settings[key] = value;

        var ex = Assert.ThrowsAny<Exception>(() => EdgeTestHost.Create(settings).DisposeAsync().AsTask().GetAwaiter().GetResult());

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnFx3uDevice_StartsWithTheMc1eDriver()
    {
        var settings = TwoDevices(intervalA: 1000, intervalB: 1000);
        settings["Edge:Channels:1:Name"] = "Fx3";
        settings["Edge:Channels:1:Driver"] = "Mc1E";
        settings["Edge:Channels:1:Devices:0:Name"] = "Old";
        settings["Edge:Channels:1:Devices:0:Model"] = "FX3U";
        settings["Edge:Channels:1:Devices:0:Host"] = "127.0.0.9";
        settings["Edge:Channels:1:Devices:0:Port"] = ClosedPort().ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings["Edge:Channels:1:Devices:0:Tags:0:Name"] = "D0";
        settings["Edge:Channels:1:Devices:0:Tags:0:Address"] = "D0";
        settings["Edge:Channels:1:Devices:0:Tags:0:DataType"] = "Int";

        await using var edge = EdgeTestHost.Create(settings);
        await edge.Host.StartAsync();

        var old = edge.Services.GetRequiredService<PlcDeviceRegistry>().Devices.Single(d => d.Name == "Old");
        Assert.IsType<Drivers.Mc1e.MitsubishiMc1eDriver>(old.Driver);
        Assert.Equal(["Old.D0"], old.Catalog.Tags.Select(t => t.TagId));
        await edge.Host.StopAsync();
    }

    private static Dictionary<string, string?> TwoDevices(int intervalA, int intervalB) => EdgeTestHost.MultiDevice(new Dictionary<string, string?>
    {
        ["Edge:Channels:0:Name"] = "S7",
        ["Edge:Channels:0:Driver"] = "S7",
        ["Edge:Channels:0:Devices:0:Name"] = "A",
        ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
        ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1",
        ["Edge:Channels:0:Devices:0:Port"] = "10201",
        ["Edge:Channels:0:Devices:0:PollIntervalMs"] = intervalA.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Edge:Channels:0:Devices:0:Tags:0:Name"] = "Speed",
        ["Edge:Channels:0:Devices:0:Tags:0:Address"] = "DB1.DBW2",
        ["Edge:Channels:0:Devices:0:Tags:0:DataType"] = "Int",
        ["Edge:Channels:0:Devices:1:Name"] = "B",
        ["Edge:Channels:0:Devices:1:Model"] = "S7-1200",
        ["Edge:Channels:0:Devices:1:Host"] = "127.0.0.1",
        ["Edge:Channels:0:Devices:1:Port"] = "10202",
        ["Edge:Channels:0:Devices:1:PollIntervalMs"] = intervalB.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Edge:Channels:0:Devices:1:Tags:0:Name"] = "Speed",
        ["Edge:Channels:0:Devices:1:Tags:0:Address"] = "DB1.DBW2",
        ["Edge:Channels:0:Devices:1:Tags:0:DataType"] = "Int",
    });

    private static Task<EdgeTestHost> StartAsync(
        Dictionary<string, string?> settings, Dictionary<string, CountingDriver> drivers, FakeTimeProvider clock) =>
        StartAsync(settings, new FakeDriverFactory(device => drivers[device.Name]), clock);

    private static async Task<EdgeTestHost> StartAsync(Dictionary<string, string?> settings, FakeDriverFactory factory, FakeTimeProvider clock)
    {
        var edge = EdgeTestHost.Create(
            settings,
            services =>
            {
                services.AddSingleton<IPlcDriverFactory>(factory);
                services.AddSingleton<TimeProvider>(clock);
            },
            runAgentServices: true);
        await edge.Host.StartAsync();
        return edge;
    }

    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static PollCycle Cycle(string tagId, int readMs) => new(
        Start, false, [new TagReading(tagId, null, TelemetryQuality.Good, Start, null)], TimeSpan.FromMilliseconds(readMs));

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), $"timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    /// <summary>Counts reads, can hang, takes <see cref="ReadTime"/> on the fake clock, and reports a request count.</summary>
    private sealed class CountingDriver(FakeTimeProvider clock) : IPlcDriver, IPlcReadStatistics, IAsyncDisposable
    {
        private readonly List<DateTimeOffset> _readTimes = [];
        private TaskCompletionSource? _hang;

        public TimeSpan ReadTime { get; init; }

        public int Requests { get; init; } = 1;

        public int LastBatchRequests => Requests;

        public bool Disposed { get; private set; }

        public int Reads
        {
            get
            {
                lock (_readTimes)
                {
                    return _readTimes.Count;
                }
            }
        }

        public IReadOnlyList<DateTimeOffset> ReadTimes
        {
            get
            {
                lock (_readTimes)
                {
                    return [.. _readTimes];
                }
            }
        }

        public void Hang() => _hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _hang?.TrySetResult();

        public async Task<IReadOnlyList<PlcReadResult>> ReadAsync(IReadOnlyList<ResolvedTag> tags, CancellationToken ct)
        {
            lock (_readTimes)
            {
                _readTimes.Add(clock.GetUtcNow());
            }

            if (_hang is { } hang)
            {
                await hang.Task.WaitAsync(ct);
            }

            // Moves the shared fake clock only inside this read: the poller measures it as the read duration.
            if (ReadTime > TimeSpan.Zero)
            {
                clock.Advance(ReadTime);
            }

            return [.. tags.Select(_ => PlcReadResult.Ok((short)1))];
        }

        public Task WriteAsync(IPlcAddress address, PlcDataType type, object value, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> IsReachableAsync(CancellationToken ct) => Task.FromResult(true);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
