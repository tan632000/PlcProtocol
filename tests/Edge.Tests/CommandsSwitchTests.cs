using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Messaging;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet.Server;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// Remote writes are off unless <c>Edge:Commands:Enabled</c> is true. Every case uses a writable tag and a driver that
/// accepts writes, so with the listener active a command would be written and answered.
/// </summary>
public class CommandsSwitchTests
{
    private const string CommandsTopic = "iiot/test-edge/commands";
    private const string ResultsTopic = "iiot/test-edge/command-results";

    [Fact]
    public async Task Default_DoesNotSubscribeExecuteOrAnswer()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var subscriptions = RecordSubscriptions(broker);
        var driver = new FakePlcDriver();
        var logs = new LogCapture();
        await using var edge = await StartEdgeAsync(broker, driver, logs, enabled: null); // key absent: production default

        await broker.PublishAsync(CommandsTopic, ContractJson.Serialize(Command()));
        await Task.Delay(2000);

        Assert.Empty(driver.Writes);
        Assert.DoesNotContain(broker.Received, m => m.Topic == ResultsTopic);
        Assert.DoesNotContain(subscriptions, s => s == ("test-edge", CommandsTopic));
        Assert.Contains(subscriptions, s => s == ("test-edge", "iiot/test-edge/telemetry-acks")); // the Edge did subscribe
        Assert.Contains(logs.Entries, e => e.Contains("Remote writes are disabled (Edge:Commands:Enabled=false)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enabled_ExecutesAndAnswers()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var subscriptions = RecordSubscriptions(broker);
        var driver = new FakePlcDriver();
        await using var edge = await StartEdgeAsync(broker, driver, new LogCapture(), enabled: "true");

        var command = Command();
        await broker.PublishAsync(CommandsTopic, ContractJson.Serialize(command));

        await WaitUntilAsync(() => broker.Received.Any(m => m.Topic == ResultsTopic), "a command result");
        Assert.Single(driver.Writes);
        Assert.Contains(subscriptions, s => s == ("test-edge", CommandsTopic));
    }

    [Fact]
    public async Task RestartFromWriteMode_QueuedCommandsDiscarded_NotRedelivered()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var unsubscriptions = new ConcurrentQueue<(string ClientId, string Topic)>();
        broker.Server.InterceptingUnsubscriptionAsync += e =>
        {
            unsubscriptions.Enqueue((e.ClientId, e.Topic));
            return Task.CompletedTask;
        };

        // A write-mode run leaves the subscription in the persistent session.
        await using (await StartEdgeAsync(broker, new FakePlcDriver(), new LogCapture(), enabled: "true"))
        {
        }

        // Queued for the offline session; the next run has commands switched off.
        for (var i = 0; i < 30; i++)
        {
            await broker.PublishAsync(CommandsTopic, ContractJson.Serialize(Command()));
        }

        var driver = new FakePlcDriver();
        var logs = new LogCapture();
        var earlier = broker.Received.Count; // the write-mode run's telemetry belongs to another stream
        await using (var edge = await StartEdgeAsync(broker, driver, logs, enabled: null))
        {
            await WaitUntilAsync(() => logs.Count("Discarded a message on retired topic") >= 30, "30 queued commands discarded");
            await WaitUntilAsync(() => unsubscriptions.Contains(("test-edge", CommandsTopic)), "the commands topic unsubscribed");

            // Telemetry and its acks still flow.
            var store = edge.Services.GetRequiredService<Buffering.TelemetryStore>();
            await store.AppendBatchAsync([new Buffering.TelemetrySample("Speed", JsonSerializer.SerializeToElement(1), TelemetryQuality.Good, DateTimeOffset.UtcNow)], default);

            // The poller's start-up cycle may queue a sample too, at any moment: acknowledge up to the newest one
            // published until the buffer is empty.
            var acked = -1L;
            await WaitUntilAsync(() =>
            {
                if (store.CountAsync(default).GetAwaiter().GetResult() == 0)
                {
                    return true;
                }

                var newest = broker.Received.Skip(earlier).Where(m => m.Topic == "iiot/test-edge/telemetry")
                    .Select(m => ContractJson.Deserialize<TelemetryPayload>(System.Text.Encoding.UTF8.GetString(m.Payload.ToArray())))
                    .MaxBy(p => p.Sequence);
                if (newest is not null && newest.Sequence > acked)
                {
                    acked = newest.Sequence;
                    broker.PublishAsync("iiot/test-edge/telemetry-acks", ContractJson.Serialize(new TelemetryAck
                    {
                        SchemaVersion = ContractJson.SchemaVersion,
                        EdgeId = "test-edge",
                        StreamId = newest.StreamId,
                        FromSequence = 0,
                        ToSequence = newest.Sequence,
                    })).GetAwaiter().GetResult();
                }

                return false;
            }, "the acks removed every sample");

            Assert.Empty(driver.Writes);
            Assert.DoesNotContain(broker.Received, m => m.Topic == ResultsTopic);
            Assert.Equal(0, logs.Count("Ignoring a message"));
        }

        // Same broker, same session: nothing on the retired topic comes back.
        var later = new LogCapture();
        await using (await StartEdgeAsync(broker, new FakePlcDriver(), later, enabled: null))
        {
            await Task.Delay(2000);
            Assert.Equal(0, later.Count("Discarded a message on retired topic"));
            Assert.Equal(0, later.Count("Ignoring a message"));
        }
    }

    [Fact]
    public async Task NonBooleanValue_FailsStartNamingTheKey()
    {
        await using var edge = EdgeTestHost.Create(new Dictionary<string, string?> { ["Edge:Commands:Enabled"] = "yes" });

        var error = await Record.ExceptionAsync(() => edge.Host.StartAsync());

        Assert.NotNull(error);
        Assert.Contains("Edge:Commands:Enabled", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedAppSettings_KeepCommandsOff()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IndustrialIot.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "src", "Edge", "appsettings.json")));
        Assert.False(document.RootElement.GetProperty("Edge").GetProperty("Commands").GetProperty("Enabled").GetBoolean());
    }

    /// <param name="enabled">The <c>Edge:Commands:Enabled</c> value; null removes the key (the test host sets it to true).</param>
    private static async Task<EdgeTestHost> StartEdgeAsync(TestMqttBroker broker, FakePlcDriver driver, LogCapture logs, string? enabled)
    {
        var settings = broker.EdgeSettings();
        settings["Edge:Commands:Enabled"] = enabled;
        settings["Edge:Tags:0:TagId"] = "Speed";
        settings["Edge:Tags:0:Address"] = "DB1.DBW2";
        settings["Edge:Tags:0:DataType"] = "Int";
        settings["Edge:Tags:0:Writable"] = "true";
        settings["Edge:Poll:IntervalMs"] = "3600000"; // keep telemetry out of the way

        var edge = EdgeTestHost.Create(
            settings,
            services =>
            {
                services.AddSingleton<IPlcDriver>(driver);
                services.AddSingleton<ILoggerProvider>(logs);
            },
            runAgentServices: true);
        await edge.Host.StartAsync();

        // Signalled only after the subscriptions are in place.
        await edge.Services.GetRequiredService<MqttConnection>().WaitForConnectionAsync(default).WaitAsync(TimeSpan.FromSeconds(15));
        return edge;
    }

    private static ConcurrentQueue<(string ClientId, string Topic)> RecordSubscriptions(TestMqttBroker broker)
    {
        var subscriptions = new ConcurrentQueue<(string ClientId, string Topic)>();
        broker.Server.InterceptingSubscriptionAsync += e =>
        {
            subscriptions.Enqueue((e.ClientId, e.TopicFilter.Topic));
            return Task.CompletedTask;
        };
        return subscriptions;
    }

    private static DeviceCommand Command() => new()
    {
        SchemaVersion = 1,
        CommandId = Guid.NewGuid(),
        EdgeId = "test-edge",
        TagId = "Speed",
        TargetValue = JsonDocument.Parse("42").RootElement.Clone(),
        IssuedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        IssuedBy = "test-operator",
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), $"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyList<string> Entries => [.. _entries];

        public int Count(string fragment) => _entries.Count(e => e.Contains(fragment, StringComparison.Ordinal));

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
                entries.Enqueue(formatter(state, exception));
        }
    }
}
