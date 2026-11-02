using System.Diagnostics;
using IndustrialIot.Edge.Monitoring;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// One PLC that is down, silent or lost mid-run does not slow or stop another: the real drivers talk TCP to fake PLCs,
/// the real poller runs on the real clock, and the healthy device must keep its own interval.
/// </summary>
public sealed class DeviceIsolationTests
{
    // The dead PLC's timeout is ten of the healthy PLC's intervals, so any coupling shows up as a gap of seconds.
    private const int DeadTimeoutMs = 2000;
    private const int HealthyIntervalMs = 200;
    private static readonly TimeSpan Watch = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxHealthyGap = TimeSpan.FromMilliseconds(1000);

    [Theory]
    [InlineData(FakeS7Mode.ClosedPort)]
    [InlineData(FakeS7Mode.BlackHole)]
    [InlineData(FakeS7Mode.HandshakeThenSilent)]
    public async Task ADeadS7_DoesNotDelayTheFx5u(FakeS7Mode mode)
    {
        await using var s7 = FakeS7Server.Start(mode);
        await using var fx5u = new FakeSlmpServer();
        fx5u.Memory[100] = 1234;
        await using var edge = await StartAsync(s7.Port, fx5u.Port);
        var snapshot = edge.Services.GetRequiredService<LiveTagSnapshot>();

        var cycles = await WatchAsync(snapshot, "Press", Watch);

        AssertKeptItsInterval(cycles, "Press");
        Assert.All(cycles, c => Assert.Equal(1234, c.Readings.Single().Value!.Value.GetInt32()));
        await WaitUntilAsync(() => snapshot.Devices.TryGetValue("Line", out var line) && line.PlcUnavailable, "the S7 reported unavailable");
        Assert.False(snapshot.Devices["Press"].PlcUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnFx5uLostMidRun_DoesNotStopTheS7(bool silent)
    {
        await using var s7 = FakeS7Server.StartWithMemory(pduSize: 240);
        var fx5u = new FakeSlmpServer();
        var fx5uDisposed = false;
        try
        {
            await using var edge = await StartAsync(s7.Port, fx5u.Port, healthy: "Line");
            var snapshot = edge.Services.GetRequiredService<LiveTagSnapshot>();
            await WaitUntilAsync(() => snapshot.LastGoodUtc.ContainsKey("Press") && snapshot.LastGoodUtc.ContainsKey("Line"), "both PLCs read once");

            if (silent)
            {
                // A pulled cable: the connection stays open but no reply ever comes, so every read waits its timeout.
                fx5u.Mode = FakeSlmpMode.BlackHole;
            }
            else
            {
                // The FX5U is switched off: its socket is closed and nothing listens any more.
                await fx5u.DisposeAsync();
                fx5uDisposed = true;
            }

            var cycles = await WatchAsync(snapshot, "Line", Watch);

            AssertKeptItsInterval(cycles, "Line");
            await WaitUntilAsync(() => snapshot.Devices["Press"].PlcUnavailable, "the FX5U reported unavailable");
            Assert.False(snapshot.Devices["Line"].PlcUnavailable);
        }
        finally
        {
            if (!fx5uDisposed)
            {
                await fx5u.DisposeAsync();
            }
        }
    }

    /// <summary>S7 device "Line" and FX5U device "Press"; the healthy one polls fast, the other has the long timeout.</summary>
    private static async Task<EdgeTestHost> StartAsync(int s7Port, int fx5uPort, string healthy = "Press")
    {
        string Interval(string device) => (device == healthy ? HealthyIntervalMs : 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Timeout(string device) => (device == healthy ? 1000 : DeadTimeoutMs).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var edge = EdgeTestHost.Create(
            EdgeTestHost.MultiDevice(new Dictionary<string, string?>
            {
                ["Edge:Channels:0:Name"] = "S7",
                ["Edge:Channels:0:Driver"] = "S7",
                ["Edge:Channels:0:Devices:0:Name"] = "Line",
                ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
                ["Edge:Channels:0:Devices:0:Host"] = "127.0.0.1",
                ["Edge:Channels:0:Devices:0:Port"] = s7Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Edge:Channels:0:Devices:0:PollIntervalMs"] = Interval("Line"),
                ["Edge:Channels:0:Devices:0:TimeoutMs"] = Timeout("Line"),
                ["Edge:Channels:0:Devices:0:Tags:0:Name"] = "Speed",
                ["Edge:Channels:0:Devices:0:Tags:0:Address"] = "DB1.DBW2",
                ["Edge:Channels:0:Devices:0:Tags:0:DataType"] = "Int",
                ["Edge:Channels:1:Name"] = "Fx",
                ["Edge:Channels:1:Driver"] = "Slmp3E",
                ["Edge:Channels:1:Devices:0:Name"] = "Press",
                ["Edge:Channels:1:Devices:0:Model"] = "FX5U",
                ["Edge:Channels:1:Devices:0:Host"] = "127.0.0.1",
                ["Edge:Channels:1:Devices:0:Port"] = fx5uPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Edge:Channels:1:Devices:0:PollIntervalMs"] = Interval("Press"),
                ["Edge:Channels:1:Devices:0:TimeoutMs"] = Timeout("Press"),
                ["Edge:Channels:1:Devices:0:Tags:0:Name"] = "Count",
                ["Edge:Channels:1:Devices:0:Tags:0:Address"] = "D100",
                ["Edge:Channels:1:Devices:0:Tags:0:DataType"] = "Int",
            }),
            runAgentServices: true);
        await edge.Host.StartAsync();
        return edge;
    }

    /// <summary>The distinct cycles <paramref name="device"/> completes during <paramref name="duration"/>.</summary>
    private static async Task<List<PollCycle>> WatchAsync(LiveTagSnapshot snapshot, string device, TimeSpan duration)
    {
        var cycles = new List<PollCycle>();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            if (snapshot.Devices.TryGetValue(device, out var cycle) && (cycles.Count == 0 || cycles[^1].CompletedUtc != cycle.CompletedUtc))
            {
                cycles.Add(cycle);
            }

            await Task.Delay(5);
        }

        return cycles;
    }

    private static void AssertKeptItsInterval(List<PollCycle> cycles, string device)
    {
        // 3 s at 200 ms is 15 cycles; 8 leaves room for a slow CI machine and still rules out waiting on a 2 s timeout.
        Assert.True(cycles.Count >= 8, $"{device} completed only {cycles.Count} cycles in {Watch.TotalSeconds} s");
        Assert.All(cycles, c => Assert.False(c.PlcUnavailable, $"{device} was unavailable at {c.CompletedUtc:O}"));
        Assert.All(cycles, c => Assert.Equal(TelemetryQuality.Good, c.Readings.Single().Quality));
        var maxGap = cycles.Zip(cycles.Skip(1), (x, y) => y.CompletedUtc - x.CompletedUtc).Max();
        Assert.True(maxGap < MaxHealthyGap, $"{device}'s longest gap was {maxGap.TotalMilliseconds} ms");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), $"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
