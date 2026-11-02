using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Messaging;
using IndustrialIot.Edge.Monitoring;
using IndustrialIot.Edge.Polling;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MQTTnet.Protocol;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// The Edge reports its PLCs and tags on <c>iiot/{edgeId}/inventory</c>: complete rounds of parts of at most 12 KiB,
/// on connect, on a device state change (at most one per 5 s), every 60 s and after a reconnect, never touching
/// telemetry. The real <see cref="MqttConnection"/> talks to the in-process broker; the poller is removed and the
/// tests publish poll cycles themselves.
/// </summary>
public class InventoryPublisherTests
{
    private const string Topic = "iiot/test-edge/inventory";
    private const int MaxPartBytes = 12 * 1024;

    [Fact]
    public async Task StartRound_ListsEveryDeviceAndTag_InConfigurationOrder_WithoutNetworkDetail()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        await using var edge = await StartEdgeAsync(broker, TwoDevices());

        var round = await WaitForRoundAsync(broker, r => true);

        Assert.All(round.Messages, m => Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, m.QualityOfServiceLevel));
        Assert.Equal(0, round.Parts[0].Round);
        Assert.All(round.Messages, m => Assert.True(m.Payload.Length <= MaxPartBytes));
        Assert.True(round.Parts[0].CommandsEnabled);

        var devices = round.Devices;
        Assert.Equal(["Line", "Press"], devices.Select(d => d.Name));
        Assert.Equal(("S7-1200", true, false, InventoryDeviceState.Waiting, 2), (devices[0].Model, devices[0].Enabled, devices[0].WritesEnabled, devices[0].State, devices[0].TagCount));
        Assert.Equal(("FX5U", false, InventoryDeviceState.Disabled, 1), (devices[1].Model, devices[1].Enabled, devices[1].State, devices[1].TagCount));
        Assert.Null(devices[0].LastGoodUtc);
        Assert.Null(devices[0].LastError);

        var line = round.TagsOf("Line");
        Assert.Equal(["Line.Speed", "Line.Batch"], line.Select(t => t.TagId));
        Assert.Equal(("Speed", "Int", true, 0m, 3000m, "Belt speed"), (line[0].Name, line[0].DataType, line[0].Writable, line[0].Min, line[0].Max, line[0].Description));
        Assert.Equal(("Batch", "String", 20, (string?)null), (line[1].Name, line[1].DataType, line[1].Length, line[1].Description));
        Assert.Equal(["Press.Count"], round.TagsOf("Press").Select(t => t.TagId));

        var raw = string.Concat(round.Messages.Select(m => Encoding.UTF8.GetString(m.Payload)));
        Assert.DoesNotContain("10.9.8.7", raw);
        Assert.DoesNotContain("DB1.DBW2", raw);
        Assert.DoesNotContain("D100", raw);
    }

    [Fact]
    public async Task SinglePlcConfiguration_SendsTheTagIdAsNameAndNoDescription()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        await using var edge = await StartEdgeAsync(broker, new Dictionary<string, string?>
        {
            ["Edge:Tags:0:Description"] = "ignored on a single-PLC Edge",
        });

        var round = await WaitForRoundAsync(broker, r => true);

        Assert.Equal(["Plc1"], round.Devices.Select(d => d.Name));
        var tag = Assert.Single(round.TagsOf("Plc1"));
        Assert.Equal(("Line1.Speed", "Line1.Speed", (string?)null), (tag.TagId, tag.Name, tag.Description));
    }

    [Fact]
    public async Task Descriptions_AreNormalized_SoTheRoundValidates()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var emojiAtTheEnd = new string('d', 255) + "😀"; // 257 units; unit 256 is a high surrogate
        await using var edge = await StartEdgeAsync(broker, OneS7Device(6, i => i switch
        {
            0 => "\n",
            1 => "   ",
            2 => "a\tb",
            3 => emojiAtTheEnd,
            4 => "lone \uD800 surrogate", // only an environment variable can carry one
            _ => "keeps 😀 pairs",
        }));

        var round = await WaitForRoundAsync(broker, r => true);

        var descriptions = round.TagsOf("Line").Select(t => t.Description).ToList();
        Assert.Equal([null, null, "a b", new string('d', 255), "lone   surrogate", "keeps 😀 pairs"], descriptions);
    }

    [Fact]
    public async Task ModelSpelledLikeTheLookupAllows_IsSentWithTheCatalogName()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var settings = TwoDevices();
        settings["Edge:Channels:0:Devices:0:Model"] = " s7-1200 ";
        settings["Edge:Channels:1:Devices:0:Model"] = "fx5u";
        await using var edge = await StartEdgeAsync(broker, settings);

        var round = await WaitForRoundAsync(broker, r => true);

        Assert.Equal(["S7-1200", "FX5U"], round.Devices.Select(d => d.Model));
    }

    [Fact]
    public async Task ThreeDevicesOfAHundredTags_WithLongDescriptions_ArePackedInto12KiBParts()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        await using var edge = await StartEdgeAsync(broker, Devices(3, 100, _ => new string('x', 256)));

        var round = await WaitForRoundAsync(broker, r => true);

        Assert.True(round.Parts.Count > 3, $"expected several parts, got {round.Parts.Count}");
        Assert.All(round.Messages, m => Assert.True(m.Payload.Length <= MaxPartBytes, $"part of {m.Payload.Length} bytes"));
        Assert.Equal(["D0", "D1", "D2"], round.Devices.Select(d => d.Name));
        foreach (var device in round.Devices)
        {
            Assert.Equal(Enumerable.Range(0, 100).Select(i => $"{device.Name}.T{i:000}"), round.TagsOf(device.Name).Select(t => t.TagId));
        }
    }

    [Fact]
    public async Task RoundOverTwoHundredParts_PublishesNothing_AndWarnsOncePerTenMinutes()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new LogCapture();
        // Non-ASCII text is escaped as \uXXXX on the wire: 256 of them make each tag about 1.6 KiB.
        await using var edge = await StartEdgeAsync(broker, Devices(2, 750, _ => new string('é', 256)), clock, logs);
        var connection = edge.Services.GetRequiredService<MqttConnection>();
        await WaitUntilAsync(() => connection.IsConnected, "connected");

        await AdvanceUntilAsync(clock, () => Warnings(logs, "200").Count == 1, "first warning", 5);
        await AdvanceAsync(clock, 130); // two heartbeats later: still skipped, no new warning

        Assert.Empty(Inventory(broker));
        Assert.Single(Warnings(logs, "200"));
        Assert.Contains("parts", Warnings(logs, "200")[0]);

        await AdvanceAsync(clock, 600);
        Assert.Equal(2, Warnings(logs, "200").Count);
    }

    [Fact]
    public async Task StateChange_SendsARoundWithinFiveSeconds_AndFlipsAreDebounced()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var edge = await StartEdgeAsync(broker, TwoDevices(), clock);
        var snapshot = edge.Services.GetRequiredService<LiveTagSnapshot>();
        await WaitForRoundAsync(broker, r => r.Parts[0].Round == 0);
        await AdvanceAsync(clock, 10);

        snapshot.Publish("Line", Cycle(clock, unavailable: true));
        await AdvanceUntilAsync(clock, () => Rounds(broker).Count == 2, "a round after the state change", 5);
        Assert.Equal(InventoryDeviceState.Unavailable, Rounds(broker)[1].Devices[0].State);
        Assert.Equal(InventoryDeviceError.Unavailable, Rounds(broker)[1].Devices[0].LastError);

        // Two flips within 5 s: one round, carrying the latest state.
        snapshot.Publish("Line", Cycle(clock, unavailable: false));
        await AdvanceAsync(clock, 1);
        snapshot.Publish("Line", Cycle(clock, unavailable: true));
        await AdvanceAsync(clock, 1);
        snapshot.Publish("Line", Cycle(clock, unavailable: false));
        await AdvanceUntilAsync(clock, () => Rounds(broker).Count == 3, "one round for the flips", 6);
        await AdvanceAsync(clock, 10);
        Assert.Equal(3, Rounds(broker).Count);
        Assert.Equal(InventoryDeviceState.Connected, Rounds(broker)[2].Devices[0].State);
    }

    [Fact]
    public async Task LastErrorChange_RidesTheNextHeartbeat_AndHeartbeatsCountRoundsUp()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var edge = await StartEdgeAsync(broker, TwoDevices(), clock);
        var snapshot = edge.Services.GetRequiredService<LiveTagSnapshot>();
        snapshot.Publish("Line", Cycle(clock, unavailable: false));
        await AdvanceUntilAsync(clock, () => Rounds(broker).Any(r => r.Devices[0].State == InventoryDeviceState.Connected), "connected round", 10);
        var before = Rounds(broker).Count;

        snapshot.Publish("Line", Cycle(clock, unavailable: false, badTag: true));
        await AdvanceAsync(clock, 30);
        Assert.Equal(before, Rounds(broker).Count); // no extra round for lastError alone

        await AdvanceUntilAsync(clock, () => Rounds(broker).Count == before + 1, "heartbeat", 40);
        var heartbeat = Rounds(broker)[^1];
        Assert.Equal(InventoryDeviceError.TagErrors, heartbeat.Devices[0].LastError);
        Assert.Equal(Rounds(broker)[^2].Parts[0].Round + 1, heartbeat.Parts[0].Round);
        Assert.Equal(Rounds(broker)[^2].Parts[0].BootId, heartbeat.Parts[0].BootId);
    }

    [Fact]
    public async Task Reconnect_SendsARoundAtOnce_AndNothingIsAttemptedWhileDisconnected()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new LogCapture();
        await using var edge = await StartEdgeAsync(broker, TwoDevices(), clock, logs);
        var connection = edge.Services.GetRequiredService<MqttConnection>();
        await WaitForRoundAsync(broker, r => r.Parts[0].Round == 0);

        await broker.StopServerAsync();
        await WaitUntilAsync(() => !connection.IsConnected, "Edge notices the outage");
        await AdvanceAsync(clock, 130); // two heartbeats due while disconnected
        Assert.Empty(Warnings(logs, "inventory"));

        await broker.StartServerAsync();
        await AdvanceUntilAsync(clock, () => connection.IsConnected, "reconnected", 60);
        await AdvanceUntilAsync(clock, () => Rounds(broker).Count == 2, "a round right after the reconnect", 2);
        Assert.Equal(1, Rounds(broker)[1].Parts[0].Round);

        // A short outage, well before the next heartbeat: the reconnect alone sends a round.
        var generation = connection.ConnectionGeneration;
        await broker.StopServerAsync();
        await WaitUntilAsync(() => !connection.IsConnected, "second outage noticed");
        await broker.StartServerAsync();
        await AdvanceUntilAsync(clock, () => connection.ConnectionGeneration > generation, "reconnected again", 20);
        await AdvanceUntilAsync(clock, () => Rounds(broker).Count == 3, "a round right after the second reconnect", 2);
        Assert.Equal(2, Rounds(broker)[2].Parts[0].Round);
    }

    [Fact]
    public async Task RefusedInventory_LeavesTelemetryFlowing_AndWarnsOncePerTenMinutes()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new LogCapture();
        await using var edge = await StartEdgeAsync(broker, TwoDevices(), clock, logs);
        broker.Server.InterceptingPublishAsync += e =>
        {
            if (e.ApplicationMessage.Topic == Topic)
            {
                e.ProcessPublish = false;
                e.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
            }

            return Task.CompletedTask;
        };
        var connection = edge.Services.GetRequiredService<MqttConnection>();
        await WaitUntilAsync(() => connection.IsConnected, "connected");

        await AdvanceUntilAsync(clock, () => Warnings(logs, "inventory").Count == 1, "first refusal logged", 5);
        await edge.Services.GetRequiredService<TelemetryStore>().AppendBatchAsync(
            [new TelemetrySample("Line.Speed", JsonSerializer.SerializeToElement(7), TelemetryQuality.Good, clock.GetUtcNow())], default);
        await AdvanceUntilAsync(clock, () => broker.Received.Any(m => m.Topic == "iiot/test-edge/telemetry"), "telemetry still published", 10);

        await AdvanceAsync(clock, 130);
        Assert.Single(Warnings(logs, "inventory"));
        await AdvanceAsync(clock, 600);
        Assert.Equal(2, Warnings(logs, "inventory").Count);
    }

    [Fact]
    public async Task SettingsApply_StopsTheOldPublisherFirst_AndTheNextRoundHasANewBoot()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var contentRoot = Path.Combine(Path.GetTempPath(), "edge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var settings = SupervisorSettings(broker);
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = contentRoot });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddEdgeSupervisor(options =>
        {
            options.BindRetry = TimeSpan.FromSeconds(1);
            options.ConfigureInner = services =>
            {
                EdgeTestHost.WithoutHostedService<TagPoller>(services);
                EdgeTestHost.WithoutHostedService<Commands.CommandListener>(services);
            };
        });
        using var outer = builder.Build();
        try
        {
            await outer.StartAsync();
            var first = await WaitForRoundAsync(broker, r => true);

            var restarter = outer.Services.GetRequiredService<EdgeRestarter>();
            Assert.True(restarter.TryRequest(SupervisorFile("New.One"), out var applyId));
            restarter.Release(applyId);

            var second = await WaitForRoundAsync(broker, r => r.Parts[0].BootId != first.Parts[0].BootId, TimeSpan.FromSeconds(30));
            Assert.Equal(0, second.Parts[0].Round);
            Assert.Equal(["New.One"], second.TagsOf("Plc1").Select(t => t.TagId));

            // Nothing from the old boot after the new boot's first part.
            var messages = Inventory(broker);
            var firstOfNew = messages.FindIndex(m => m.Part.BootId == second.Parts[0].BootId);
            Assert.DoesNotContain(messages.Skip(firstOfNew), m => m.Part.BootId == first.Parts[0].BootId);
            await outer.StopAsync();
        }
        finally
        {
            EdgeTestHost.DeleteDirectory(contentRoot);
        }
    }

    private static async Task<EdgeTestHost> StartEdgeAsync(
        TestMqttBroker broker, Dictionary<string, string?> overrides, FakeTimeProvider? clock = null, LogCapture? logs = null)
    {
        var settings = broker.EdgeSettings();
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var edge = EdgeTestHost.Create(
            settings,
            services =>
            {
                EdgeTestHost.WithoutHostedService<TagPoller>(services);
                EdgeTestHost.WithoutHostedService<Commands.CommandListener>(services);
                services.AddSingleton<IPlcDriverFactory>(new FakeDriverFactory(_ => new FakePlcDriver()));
                if (clock is not null)
                {
                    services.AddSingleton<TimeProvider>(clock);
                }

                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            },
            runAgentServices: true);
        await edge.Host.StartAsync();
        return edge;
    }

    private static Dictionary<string, string?> TwoDevices() => EdgeTestHost.MultiDevice(new Dictionary<string, string?>
    {
        ["Edge:Channels:0:Name"] = "S7",
        ["Edge:Channels:0:Driver"] = "S7",
        ["Edge:Channels:0:Devices:0:Name"] = "Line",
        ["Edge:Channels:0:Devices:0:Model"] = "S7-1200",
        ["Edge:Channels:0:Devices:0:Host"] = "10.9.8.7",
        ["Edge:Channels:0:Devices:0:Tags:0:Name"] = "Speed",
        ["Edge:Channels:0:Devices:0:Tags:0:Address"] = "DB1.DBW2",
        ["Edge:Channels:0:Devices:0:Tags:0:DataType"] = "Int",
        ["Edge:Channels:0:Devices:0:Tags:0:Writable"] = "true",
        ["Edge:Channels:0:Devices:0:Tags:0:Min"] = "0",
        ["Edge:Channels:0:Devices:0:Tags:0:Max"] = "3000",
        ["Edge:Channels:0:Devices:0:Tags:0:Description"] = "Belt speed",
        ["Edge:Channels:0:Devices:0:Tags:1:Name"] = "Batch",
        ["Edge:Channels:0:Devices:0:Tags:1:Address"] = "DB1.DBB20",
        ["Edge:Channels:0:Devices:0:Tags:1:DataType"] = "String",
        ["Edge:Channels:0:Devices:0:Tags:1:Length"] = "20",
        ["Edge:Channels:1:Name"] = "Fx",
        ["Edge:Channels:1:Driver"] = "Slmp3E",
        ["Edge:Channels:1:Devices:0:Name"] = "Press",
        ["Edge:Channels:1:Devices:0:Model"] = "FX5U",
        ["Edge:Channels:1:Devices:0:Host"] = "10.9.8.8",
        ["Edge:Channels:1:Devices:0:Port"] = "5000",
        ["Edge:Channels:1:Devices:0:Enabled"] = "false",
        ["Edge:Channels:1:Devices:0:Tags:0:Name"] = "Count",
        ["Edge:Channels:1:Devices:0:Tags:0:Address"] = "D100",
        ["Edge:Channels:1:Devices:0:Tags:0:DataType"] = "DInt",
    });

    private static Dictionary<string, string?> OneS7Device(int tags, Func<int, string> description)
    {
        var settings = Devices(1, tags, description);
        settings["Edge:Channels:0:Devices:0:Name"] = "Line";
        for (var t = 0; t < tags; t++)
        {
            settings[$"Edge:Channels:0:Devices:0:Tags:{t}:Name"] = $"T{t:000}";
        }

        return settings;
    }

    /// <summary>S7 devices D0..Dn-1 on one channel, each with Int tags T000.. on DB1.</summary>
    private static Dictionary<string, string?> Devices(int devices, int tags, Func<int, string> description)
    {
        var channels = new Dictionary<string, string?>
        {
            ["Edge:Channels:0:Name"] = "S7",
            ["Edge:Channels:0:Driver"] = "S7",
        };
        for (var d = 0; d < devices; d++)
        {
            var key = $"Edge:Channels:0:Devices:{d}";
            channels[$"{key}:Name"] = $"D{d}";
            channels[$"{key}:Model"] = "S7-1200";
            channels[$"{key}:Host"] = $"10.9.{d}.1";
            for (var t = 0; t < tags; t++)
            {
                channels[$"{key}:Tags:{t}:Name"] = $"T{t:000}";
                channels[$"{key}:Tags:{t}:Address"] = $"DB1.DBW{2 * t}";
                channels[$"{key}:Tags:{t}:DataType"] = "Int";
                channels[$"{key}:Tags:{t}:Description"] = description(t);
            }
        }

        return EdgeTestHost.MultiDevice(channels);
    }

    private static Dictionary<string, string?> SupervisorSettings(TestMqttBroker broker)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge:EdgeId"] = "test-edge",
            ["Edge:Plc:Host"] = "127.0.0.1",
            ["Edge:Plc:TimeoutMs"] = "500",
            ["Edge:Tags:0:TagId"] = "Line1.Speed",
            ["Edge:Tags:0:Address"] = "DB1.DBW2",
            ["Edge:Tags:0:DataType"] = "Int",
            ["Edge:Buffer:Path"] = "data/edge.db",
            ["Edge:Poll:IntervalMs"] = "1000",
            ["Edge:Mqtt:ReconnectMinDelayMs"] = "200",
            ["Edge:Mqtt:ReconnectMaxDelayMs"] = "1000",
            ["Edge:Monitor:Enabled"] = "false",
        };
        foreach (var (key, value) in broker.EdgeSettings())
        {
            settings[key] = value;
        }

        return settings;
    }

    private static byte[] SupervisorFile(params string[] tagIds)
    {
        var tags = string.Join(",", tagIds.Select((id, i) => $$"""{"TagId":"{{id}}","Address":"DB1.DBW{{i * 2}}","DataType":"Int"}"""));
        return Encoding.UTF8.GetBytes(
            "{\"SchemaVersion\":1,\"Edge\":{\"Plc\":{\"Host\":\"127.0.0.1\",\"TimeoutMs\":500},"
            + "\"Poll\":{\"IntervalMs\":1000},\"Tags\":[" + tags + "]}}");
    }

    private static PollCycle Cycle(FakeTimeProvider clock, bool unavailable, bool badTag = false) => new(
        clock.GetUtcNow(),
        unavailable,
        [new TagReading("Line.Speed", null, unavailable || badTag ? TelemetryQuality.Bad : TelemetryQuality.Good, clock.GetUtcNow(), null)],
        TimeSpan.FromMilliseconds(5));

    private static List<(EdgeInventory Part, MQTTnet.MqttApplicationMessage Message)> Inventory(TestMqttBroker broker) =>
        broker.Received
            .Where(m => m.Topic == Topic)
            .Select(m => (ContractJson.Deserialize<EdgeInventory>(Encoding.UTF8.GetString(m.Payload)), m))
            .ToList();

    /// <summary>Complete rounds in the order their first part arrived.</summary>
    private static List<Round> Rounds(TestMqttBroker broker) =>
        Inventory(broker)
            .GroupBy(x => (x.Part.BootId, x.Part.Round))
            .Select(g => g.ToList())
            .Where(g => g.Select(x => x.Part.Part).Distinct().Count() == g[0].Part.PartCount)
            .Select(g => new Round(
                [.. g.DistinctBy(x => x.Part.Part).OrderBy(x => x.Part.Part).Select(x => x.Part)],
                [.. g.DistinctBy(x => x.Part.Part).OrderBy(x => x.Part.Part).Select(x => x.Message)]))
            .ToList();

    private static async Task<Round> WaitForRoundAsync(TestMqttBroker broker, Func<Round, bool> match, TimeSpan? timeout = null)
    {
        Round? found = null;
        await WaitUntilAsync(() => (found = Rounds(broker).FirstOrDefault(match)) is not null, "an inventory round", timeout);
        return found!;
    }

    private static List<string> Warnings(LogCapture logs, string containing) =>
        logs.Entries.Where(e => e.StartsWith("Warning ", StringComparison.Ordinal) && e.Contains(containing, StringComparison.OrdinalIgnoreCase)
            && e.Contains("inventory", StringComparison.OrdinalIgnoreCase)).ToList();

    private static async Task AdvanceAsync(FakeTimeProvider clock, double seconds)
    {
        for (var moved = 0.0; moved < seconds - 1e-9; moved += 0.5)
        {
            clock.Advance(TimeSpan.FromSeconds(Math.Min(0.5, seconds - moved)));
            await Task.Delay(15);
        }
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> condition, string what, double maxSeconds)
    {
        for (var moved = 0.0; !condition(); moved += 0.25)
        {
            Assert.True(moved <= maxSeconds, $"not within {maxSeconds} s of fake time: {what}");
            clock.Advance(TimeSpan.FromSeconds(0.25));
            await Task.Delay(40);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < limit, $"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    private sealed record Round(IReadOnlyList<EdgeInventory> Parts, IReadOnlyList<MQTTnet.MqttApplicationMessage> Messages)
    {
        public IReadOnlyList<InventoryDevice> Devices => [.. Parts.Where(p => p.Devices is not null).SelectMany(p => p.Devices!)];

        public IReadOnlyList<InventoryTag> TagsOf(string device) =>
            [.. Parts.Where(p => p.Tags?.Device == device).SelectMany(p => p.Tags!.Items)];
    }

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        /// <summary>"Level message exception" per entry.</summary>
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
                entries.Enqueue($"{logLevel} {formatter(state, exception)} {exception}");
        }
    }
}
