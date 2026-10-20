using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Messaging;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace IndustrialIot.Edge.Tests;

public class TelemetryPublisherTests
{
    [Fact]
    public async Task Drains_InSequenceOrder_DeletesAfterPuback()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        await using var edge = await StartEdgeAsync(broker.EdgeSettings());
        var store = edge.Services.GetRequiredService<TelemetryStore>();

        await store.AppendBatchAsync(Samples(50), default);

        await WaitUntilAsync(() => Telemetry(broker).Count >= 50, "50 samples at the broker");
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (long)i), Telemetry(broker).Select(p => p.Sequence));
        await WaitUntilAsync(async () => await store.CountAsync(default) == 0, "queue drained");
    }

    [Fact]
    public async Task BrokerOutage_BuffersThenDrainsInOrder()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        await using var edge = await StartEdgeAsync(broker.EdgeSettings());
        var store = edge.Services.GetRequiredService<TelemetryStore>();
        var connection = edge.Services.GetRequiredService<MqttConnection>();

        await store.AppendBatchAsync(Samples(5), default);
        await WaitUntilAsync(() => Telemetry(broker).Count >= 5, "first 5 samples");

        await broker.StopServerAsync();
        await WaitUntilAsync(() => !connection.IsConnected, "Edge notices the outage");
        await store.AppendBatchAsync(Samples(20), default);
        await Task.Delay(1000);
        Assert.Equal(20, await store.CountAsync(default)); // nothing lost, nothing deleted

        await broker.StartServerAsync();
        await WaitUntilAsync(() => Telemetry(broker).Count >= 25, "all 25 samples after the restart");
        Assert.Equal(Enumerable.Range(0, 25).Select(i => (long)i), Telemetry(broker).Select(p => p.Sequence));
        await WaitUntilAsync(async () => await store.CountAsync(default) == 0, "queue drained");

        // A publish that fails while connected (broker answers with a failed PUBACK, once): the row stays
        // first in line and is sent again; nothing after it overtakes it, nothing is dropped.
        var attemptsFor27 = 0;
        broker.RejectPublish = message =>
            message.Topic == "iiot/test-edge/telemetry"
            && ContractJson.Deserialize<TelemetryPayload>(Encoding.UTF8.GetString(message.Payload)).Sequence == 27
            && Interlocked.Increment(ref attemptsFor27) == 1; // reject the first attempt only
        await store.AppendBatchAsync(Samples(5), default);

        await WaitUntilAsync(() => Telemetry(broker).Count >= 30, "samples 25..29 after one rejection");
        Assert.Equal(2, attemptsFor27); // rejected once, then sent again and accepted
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (long)i), Telemetry(broker).Select(p => p.Sequence));
        await WaitUntilAsync(async () => await store.CountAsync(default) == 0, "queue drained again");
    }

    [Fact]
    public async Task CertificateValidation_RejectsWrongCaAndName()
    {
        await using var broker = await TestMqttBroker.StartAsync();

        var wrongCa = broker.EdgeSettings();
        wrongCa["Edge:Mqtt:CaCertificatePath"] = broker.UnrelatedCaPath;

        var nameMismatch = broker.EdgeSettings();
        nameMismatch["Edge:Mqtt:Host"] = "127.0.0.1"; // the certificate only names "localhost"

        var osStoreOnly = broker.EdgeSettings(trustCa: false); // the test CA is not in the OS store

        foreach (var settings in new[] { wrongCa, nameMismatch, osStoreOnly })
        {
            await using var edge = await StartEdgeAsync(settings);
            await Task.Delay(1500);

            Assert.False(edge.Services.GetRequiredService<MqttConnection>().IsConnected);
            Assert.Empty(broker.ConnectAttempts); // TLS failed before any CONNECT (and before credentials were sent)
        }

        // Control: the right CA and name connect.
        await using var good = await StartEdgeAsync(broker.EdgeSettings());
        await WaitUntilAsync(() => good.Services.GetRequiredService<MqttConnection>().IsConnected, "connection with the right CA");
    }

    [Fact]
    public async Task UnhandledMessage_NotAcknowledged()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var topic = "iiot/test-edge/commands";

        // 1. A run with a handler creates the persistent session and its subscription.
        await using (var first = await StartEdgeAsync(broker.EdgeSettings(), new ConcurrentQueue<string>()))
        {
            await WaitUntilAsync(() => Connection(first).IsConnected, "first connection");
        }

        // 2. Queued while the Edge is offline, then delivered to a run that has no handler for it.
        await broker.PublishAsync(topic, "queued-command");
        await using (var noHandler = await StartEdgeAsync(broker.EdgeSettings()))
        {
            await WaitUntilAsync(() => Connection(noHandler).IsConnected, "run without handler");
            await Task.Delay(1000);
        }

        // 3. Not acknowledged above, so the broker delivers it again to the next session.
        var handled = new ConcurrentQueue<string>();
        await using (var withHandler = await StartEdgeAsync(broker.EdgeSettings(), handled))
        {
            await WaitUntilAsync(() => handled.Contains("queued-command"), "redelivered command");
        }

        // A retained message is stale state: never delivered on subscribe.
        await using var freshBroker = await TestMqttBroker.StartAsync();
        await freshBroker.PublishAsync(topic, "retained-command", retain: true);
        var fresh = new ConcurrentQueue<string>();
        await using (var edge = await StartEdgeAsync(freshBroker.EdgeSettings(), fresh))
        {
            await WaitUntilAsync(() => Connection(edge).IsConnected, "fresh session");
            await Task.Delay(1000);
            Assert.Empty(fresh);
        }
    }

    [Fact]
    public async Task Handler_ExactTopicMatchOnly()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var handled = new ConcurrentQueue<string>();
        await using var edge = await StartEdgeAsync(broker.EdgeSettings(), handled);
        await WaitUntilAsync(() => Connection(edge).IsConnected, "connection");

        // Give the session a wildcard subscription so near-miss topics actually reach the Edge.
        await broker.Server.SubscribeAsync("test-edge", [new MqttTopicFilterBuilder().WithTopic("iiot/test-edge/#").WithAtLeastOnceQoS().Build()]);
        await broker.PublishAsync("iiot/test-edge/Commands", "wrong-case");
        await broker.PublishAsync("iiot/test-edge/commands/x", "sub-level");
        await broker.PublishAsync("iiot/test-edge/commands", "exact");

        await WaitUntilAsync(() => handled.Contains("exact"), "exact topic handled");
        await Task.Delay(300);
        Assert.Equal(["exact"], handled);
    }

    [Fact]
    public async Task BadCredentials_RetriesWithBackoff_NoSecretLeak()
    {
        const string secret = "wrong-secret-7f3a9c";
        await using var broker = await TestMqttBroker.StartAsync(password: "the-right-password");
        var settings = broker.EdgeSettings();
        settings["Edge:Mqtt:Password"] = secret;
        var logs = new LogCapture();

        await using var edge = await StartEdgeAsync(settings, logs: logs);
        await WaitUntilAsync(() => broker.ConnectAttempts.Count >= 3, "three connection attempts", TimeSpan.FromSeconds(10));

        Assert.False(Connection(edge).IsConnected);
        var attempts = broker.ConnectAttempts;
        var firstGap = attempts[1] - attempts[0];
        var secondGap = attempts[2] - attempts[1];
        Assert.True(secondGap - firstGap >= TimeSpan.FromMilliseconds(100), $"delay did not grow: {firstGap} then {secondGap}");

        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(secret, StringComparison.Ordinal));
    }

    public static TheoryData<string, string> InvalidMqttSettings() => new()
    {
        { "Edge:Mqtt:Host", "" },
        { "Edge:Mqtt:Username", "" },
        { "Edge:Mqtt:Password", "" },
        { "Edge:Mqtt:Password", "p4ss-" + new string('x', 1100) }, // too long: the value must not leak
        { "Edge:Mqtt:Port", "0" },
        { "Edge:Mqtt:ReconnectMaxDelayMs", "150" }, // below the 200 ms minimum
        { "Edge:Mqtt:CaCertificatePath", "Z:/does/not/exist.pem" },
    };

    [Theory]
    [MemberData(nameof(InvalidMqttSettings))]
    public async Task InvalidMqttConfig_FailsAtStart(string key, string value)
    {
        await using var edge = EdgeTestHost.Create(new Dictionary<string, string?> { [key] = value });

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => edge.Host.StartAsync());
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        if (value.Length >= 5)
        {
            Assert.DoesNotContain(value, ex.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Purge_RunsWhileDisconnected()
    {
        // No broker at all, and a fake clock: the hourly purge must not wait for a connection.
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var settings = new Dictionary<string, string?>();
        await using var edge = await StartEdgeAsync(settings, configure: services => services.AddSingleton<TimeProvider>(clock));
        var store = edge.Services.GetRequiredService<TelemetryStore>();

        await store.AppendBatchAsync(Samples(10), default);
        Assert.Equal(10, await store.CountAsync(default));

        clock.Advance(TimeSpan.FromHours(169)); // past MaxAgeHours (168)

        // The publisher may still be finishing its start-up purge and arm its next one relative to the
        // advanced clock, so keep time moving in one-hour steps: whichever timer it armed fires.
        await WaitUntilAsync(
            async () =>
            {
                if (await store.CountAsync(default) == 0)
                {
                    return true;
                }

                clock.Advance(TimeSpan.FromHours(1));
                return false;
            },
            "age purge without a broker");
        Assert.False(Connection(edge).IsConnected);
    }

    [Fact]
    public async Task Handler_RegisteredDuringHostStart()
    {
        await using var broker = await TestMqttBroker.StartAsync();
        var handled = new ConcurrentQueue<string>();

        // A hosted service registered after the MQTT connection subscribes from its own StartAsync,
        // as the command listener does. Repeat to give a start-order race a chance to show up.
        for (var run = 0; run < 5; run++)
        {
            await using var edge = await StartEdgeAsync(broker.EdgeSettings(), configure: services =>
                services.AddHostedService(provider => new SubscribingService(
                    provider.GetRequiredService<MqttConnection>(), provider.GetRequiredService<MqttTopics>(), handled)));
            await WaitUntilAsync(() => Connection(edge).IsConnected, $"connection in run {run}");
        }

        await using var last = await StartEdgeAsync(broker.EdgeSettings(), configure: services =>
            services.AddHostedService(provider => new SubscribingService(
                provider.GetRequiredService<MqttConnection>(), provider.GetRequiredService<MqttTopics>(), handled)));
        await WaitUntilAsync(() => Connection(last).IsConnected, "final connection");
        await broker.PublishAsync("iiot/test-edge/commands", "via-start-registration");
        await WaitUntilAsync(() => handled.Contains("via-start-registration"), "message handled");
    }

    private sealed class SubscribingService(MqttConnection connection, MqttTopics topics, ConcurrentQueue<string> handled) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            connection.RegisterSubscription(topics.Commands, (message, _) =>
            {
                handled.Enqueue(Encoding.UTF8.GetString(message.Payload));
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void AppSettings_HaveNoPassword()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IndustrialIot.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var files = Directory.GetFiles(Path.Combine(root.FullName, "src", "Edge"), "appsettings*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            Assert.False(HasPasswordKey(document.RootElement), $"{file} contains a Password key; use Edge__Mqtt__Password.");
        }
    }

    private static bool HasPasswordKey(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(p =>
            p.Name.Equals("Password", StringComparison.OrdinalIgnoreCase) || HasPasswordKey(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasPasswordKey),
        _ => false,
    };

    /// <param name="handled">When set, a handler for the commands topic records each payload.</param>
    private static async Task<EdgeTestHost> StartEdgeAsync(
        Dictionary<string, string?> settings,
        ConcurrentQueue<string>? handled = null,
        LogCapture? logs = null,
        Action<IServiceCollection>? configure = null)
    {
        // No tags: the poller idles, so only the samples a test appends are published.
        settings["Edge:Tags:0:TagId"] = null;
        settings["Edge:Tags:0:Address"] = null;
        settings["Edge:Tags:0:DataType"] = null;

        var edge = EdgeTestHost.Create(
            settings,
            services =>
            {
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }

                // These tests subscribe to the commands topic themselves; one handler per topic.
                EdgeTestHost.WithoutHostedService<Commands.CommandListener>(services);
                configure?.Invoke(services);
            },
            runAgentServices: true);

        if (handled is not null)
        {
            var connection = Connection(edge);
            connection.RegisterSubscription(edge.Services.GetRequiredService<MqttTopics>().Commands, (message, _) =>
            {
                handled.Enqueue(Encoding.UTF8.GetString(message.Payload));
                return Task.CompletedTask;
            });
        }

        await edge.Host.StartAsync();
        return edge;
    }

    private static MqttConnection Connection(EdgeTestHost edge) => edge.Services.GetRequiredService<MqttConnection>();

    private static List<TelemetryPayload> Telemetry(TestMqttBroker broker) => broker.Received
        .Where(m => m.Topic == "iiot/test-edge/telemetry")
        .Select(m => ContractJson.Deserialize<TelemetryPayload>(Encoding.UTF8.GetString(m.Payload)))
        .ToList();

    private static List<TelemetrySample> Samples(int count) => Enumerable.Range(0, count)
        .Select(i => new TelemetrySample("Line1.Speed", JsonSerializer.SerializeToElement(i), TelemetryQuality.Good, DateTimeOffset.UtcNow))
        .ToList();

    private static Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        WaitUntilAsync(() => Task.FromResult(condition()), what, timeout);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < limit, $"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        /// <summary>Formatted messages plus full exception text.</summary>
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
                entries.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}
