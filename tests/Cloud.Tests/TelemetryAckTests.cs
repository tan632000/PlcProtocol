using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Cloud.Tests.Infrastructure;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Npgsql;

namespace IndustrialIot.Cloud.Tests;

/// <summary>
/// The Cloud's <see cref="TelemetryAck"/>s are the only signal that lets an Edge delete buffered telemetry, so an
/// ack must follow every stored sample and must never follow a sample the Cloud does not hold.
/// Messages are handled in order: once a marker's ack is seen, every earlier message has been handled, so its
/// absence of an ack is final.
/// </summary>
[Collection(CloudCollection.Name)]
public class TelemetryAckTests(PostgresFixture database)
{
    private static readonly Guid Stream = Guid.NewGuid();

    [Fact]
    public async Task StoredSamples_EachAckedToItsEdge()
    {
        await using var ctx = await StartAsync(database);
        var now = DateTimeOffset.UtcNow;

        // One at a time, so each ack's latency is measured on its own, not behind a queue of earlier messages.
        for (var sequence = 1; sequence <= 250; sequence++)
        {
            var started = Stopwatch.StartNew();
            await ctx.PublishAsync("edge-a", Sample("edge-a", sequence, now));
            await WaitUntilAsync(() => ctx.Acks("edge-a").Count == sequence, $"ack {sequence}", poll: TimeSpan.FromMilliseconds(2));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"ack {sequence} took {started.Elapsed}");
        }

        var acks = ctx.Acks("edge-a");
        Assert.All(acks, ack => Assert.Equal(ack.FromSequence, ack.ToSequence)); // one sample per ack
        Assert.All(acks, ack => Assert.Equal(Stream, ack.StreamId));
        Assert.All(acks, ack => Assert.Equal("edge-a", ack.EdgeId));
        Assert.Equal(Enumerable.Range(1, 250).Select(i => (long)i), acks.Select(a => a.FromSequence).Order());
        Assert.Equal(250, await ctx.CountTelemetryAsync());
        Assert.Empty(ctx.Acks("edge-b"));
    }

    [Fact]
    public async Task Duplicate_AckedAgain()
    {
        await using var ctx = await StartAsync(database);
        var sample = Sample("edge-a", 7, DateTimeOffset.UtcNow);

        await ctx.PublishAsync("edge-a", sample);
        await WaitUntilAsync(() => ctx.Acks("edge-a").Count == 1, "first ack");

        // The Edge resends when an ack was lost; the second delivery inserts nothing but must still be acked.
        await ctx.PublishAsync("edge-a", sample);
        await WaitUntilAsync(() => ctx.Acks("edge-a").Count == 2, "ack for the duplicate");

        Assert.All(ctx.Acks("edge-a"), ack => Assert.Equal(7, ack.FromSequence));
        Assert.Equal(1, await ctx.CountTelemetryAsync());
    }

    [Fact]
    public async Task PermanentlyInvalid_Acked()
    {
        await using var ctx = await StartAsync(database);

        // Breaks the tagId rule: no later delivery of these bytes can pass, so the Edge may drop them.
        var invalidTag = Sample("edge-a", 3, DateTimeOffset.UtcNow).Replace("\"tagId\":\"t1\"", "\"tagId\":\"bad tag\"", StringComparison.Ordinal);
        Assert.Contains("bad tag", invalidTag, StringComparison.Ordinal);
        await ctx.PublishAsync("edge-a", invalidTag);

        await WaitUntilAsync(() => ctx.Acks("edge-a").Count == 1, "ack for the invalid sample");
        Assert.Equal(3, ctx.Acks("edge-a").Single().FromSequence);
        Assert.Equal(0, await ctx.CountTelemetryAsync());
    }

    [Theory]
    [InlineData("future-timestamp")]   // Edge clock ahead: valid once the clock is fixed
    [InlineData("not-allow-listed")]   // no such Edge
    [InlineData("foreign-edge-id")]    // payload claims another Edge than its topic
    [InlineData("newer-schema")]       // a newer Edge: valid once this Cloud is upgraded
    [InlineData("not-json")]           // nothing identifies the sample
    [InlineData("invalid-foreign")]    // breaks a contract rule and names another Edge: never acked to anyone
    [InlineData("duplicate-sequence")] // two sequences: which one would be deleted is ambiguous
    public async Task RecoverableOrForeignDrop_NeverAcked(string kind)
    {
        await using var ctx = await StartAsync(database);
        var now = DateTimeOffset.UtcNow;

        var (topicEdgeId, payload) = kind switch
        {
            "future-timestamp" => ("edge-a", Sample("edge-a", 1, now.AddMinutes(10))),
            "not-allow-listed" => ("edge-x", Sample("edge-x", 1, now)),
            "foreign-edge-id" => ("edge-a", Sample("edge-b", 1, now)),
            "newer-schema" => ("edge-a", Sample("edge-a", 1, now).Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)),
            "not-json" => ("edge-a", "{\"schemaVersion\":1,"),
            "invalid-foreign" => ("edge-a", Sample("edge-b", 1, now).Replace("\"tagId\":\"t1\"", "\"tagId\":\"bad tag\"", StringComparison.Ordinal)),
            "duplicate-sequence" => ("edge-a", Sample("edge-a", 1, now).Replace("\"sequence\":1,", "\"sequence\":1,\"sequence\":2,", StringComparison.Ordinal)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await ctx.PublishAsync(topicEdgeId, payload);

        await ctx.PublishAsync("edge-a", Sample("edge-a", 100, now));
        await WaitUntilAsync(() => ctx.Acks("edge-a").Any(a => a.FromSequence == 100), "marker ack");

        Assert.Equal([100L], ctx.AllAcks().Select(a => a.FromSequence));
        Assert.Equal(["iiot/edge-a/telemetry-acks"], ctx.AckTopics()); // not even to the Edge a payload names
        Assert.Equal(1, await ctx.CountTelemetryAsync());
    }

    [Theory]
    [InlineData("invalid-utf8-edge-id")]     // broken UTF-8 byte: decodes to U+FFFD, no longer this Edge
    [InlineData("lone-surrogate-edge-id")]   // "\uD800" escape: valid JSON text that no string can hold
    [InlineData("lone-surrogate-stream-id")]
    public async Task UndecodablePayload_DroppedOnce_NeverAcked(string kind)
    {
        var logs = new CapturingLoggerProvider();
        await using var ctx = await StartAsync(database, services => services.AddSingleton<ILoggerProvider>(logs));
        var now = DateTimeOffset.UtcNow;

        // Every variant also breaks a contract rule (bad tagId), so it reaches the rejection parser.
        var text = Sample("edge-a", 1, now).Replace("\"tagId\":\"t1\"", "\"tagId\":\"bad tag\"", StringComparison.Ordinal);
        var payload = kind switch
        {
            "invalid-utf8-edge-id" => Insert(Encoding.UTF8.GetBytes(text), "\"edgeId\":\"edge-a", [0xC3, 0x28]),
            "lone-surrogate-edge-id" => Encoding.UTF8.GetBytes(text.Replace("\"edgeId\":\"edge-a\"", "\"edgeId\":\"edge-a\\uD800\"", StringComparison.Ordinal)),
            "lone-surrogate-stream-id" => Encoding.UTF8.GetBytes(text.Replace($"\"streamId\":\"{Stream}\"", "\"streamId\":\"\\uDC00\"", StringComparison.Ordinal)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await ctx.PublishBytesAsync("edge-a", payload);
        await ctx.PublishAsync("edge-a", Sample("edge-a", 100, now));
        await WaitUntilAsync(() => ctx.Acks("edge-a").Any(a => a.FromSequence == 100), "marker ack");

        Assert.Equal([100L], ctx.AllAcks().Select(a => a.FromSequence));
        Assert.Equal(1, logs.Count("invalid payload"));
        Assert.Equal(0, logs.Count("it is not acknowledged")); // handled, not a poison message

        // A poison message would come back in the next session; a handled one is gone for good.
        var connection = ctx.Host.Services.GetRequiredService<CloudMqttConnection>();
        var attempts = ctx.Host.Broker.ConnectAttempts.Count;
        await connection.ForceReconnectAsync();
        await WaitUntilAsync(() => ctx.Host.Broker.ConnectAttempts.Count > attempts && connection.IsConnected, "reconnected");
        await ctx.PublishAsync("edge-a", Sample("edge-a", 101, now));
        await WaitUntilAsync(() => ctx.Acks("edge-a").Any(a => a.FromSequence == 101), "second marker ack");

        Assert.Equal(1, logs.Count("invalid payload"));
    }

    [Fact]
    public async Task StoreFailure_NoAckUntilStored()
    {
        await using var ctx = await StartAsync(database);

        ctx.Host.DatabaseFaults.Enabled = true;
        var commands = ctx.Host.DatabaseFaults.CommandCount;
        await ctx.PublishAsync("edge-a", Sample("edge-a", 1, DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => ctx.Host.DatabaseFaults.CommandCount > commands, "first store attempt");

        // Retried in place (real clock): nothing is stored, so nothing may be acked.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(ctx.AllAcks());

        ctx.Host.DatabaseFaults.Enabled = false;
        await WaitUntilAsync(() => ctx.Acks("edge-a").Count == 1, "ack after the store succeeded");
        Assert.Equal(1, await ctx.CountTelemetryAsync());
    }

    private static string Sample(string edgeId, long sequence, DateTimeOffset timestamp) =>
        ContractJson.Serialize(new TelemetryPayload
        {
            SchemaVersion = ContractJson.SchemaVersion,
            EdgeId = edgeId,
            StreamId = Stream,
            Sequence = sequence,
            TagId = "t1",
            Value = JsonSerializer.SerializeToElement(1.0),
            Quality = TelemetryQuality.Good,
            TimestampUtc = timestamp.ToUniversalTime(),
        });

    private static async Task<AckContext> StartAsync(PostgresFixture database, Action<IServiceCollection>? configureServices = null)
    {
        var host = new CloudTestHost(database, configureServices: configureServices);
        var client = host.CreateClient();
        var connection = host.Services.GetRequiredService<CloudMqttConnection>();

        // Subscribed before anything is published: an injected message without a subscriber is lost.
        await connection.FirstAttemptCompleted.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(connection.IsConnected);
        return new AckContext(host, client);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? poll = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), $"timed out waiting for: {what}");
            await Task.Delay(poll ?? TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>Inserts raw bytes right after the first occurrence of <paramref name="after"/>.</summary>
    private static byte[] Insert(byte[] bytes, string after, byte[] inserted)
    {
        var needle = Encoding.UTF8.GetBytes(after);
        for (var i = 0; i + needle.Length <= bytes.Length; i++)
        {
            if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                var at = i + needle.Length;
                return [.. bytes[..at], .. inserted, .. bytes[at..]];
            }
        }

        throw new InvalidOperationException($"'{after}' not found");
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public int Count(string fragment) => _messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed record AckContext(CloudTestHost Host, HttpClient Client) : IAsyncDisposable
    {
        public Task PublishAsync(string topicEdgeId, string payload) => Host.Broker.PublishAsync($"iiot/{topicEdgeId}/telemetry", payload);

        /// <summary>Raw bytes, for payloads a string cannot carry (invalid UTF-8).</summary>
        public Task PublishBytesAsync(string topicEdgeId, byte[] payload) =>
            Host.Broker.Server.InjectApplicationMessage(new InjectedMqttApplicationMessage(new MqttApplicationMessageBuilder()
                .WithTopic($"iiot/{topicEdgeId}/telemetry")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build()) { SenderClientId = "test-cloud" });

        /// <summary>Every ack the Cloud published, in arrival order (the test broker records the Cloud's publishes).</summary>
        public IReadOnlyList<TelemetryAck> AllAcks() =>
            [.. Host.Broker.Received.Where(m => m.Topic.EndsWith("/telemetry-acks", StringComparison.Ordinal))
                .Select(m => ContractJson.Deserialize<TelemetryAck>(Encoding.UTF8.GetString(m.Payload)))];

        public IReadOnlyList<TelemetryAck> Acks(string edgeId) =>
            [.. Host.Broker.Received.Where(m => m.Topic == $"iiot/{edgeId}/telemetry-acks")
                .Select(m => ContractJson.Deserialize<TelemetryAck>(Encoding.UTF8.GetString(m.Payload)))];

        public IReadOnlyList<string> AckTopics() =>
            [.. Host.Broker.Received.Select(m => m.Topic).Where(t => t.EndsWith("/telemetry-acks", StringComparison.Ordinal)).Distinct()];

        public async Task<int> CountTelemetryAsync()
        {
            await using var scope = Host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Telemetry.CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();

            // Each host uses a database of its own, so a pool of its own; idle pooled connections would otherwise stay
            // open on the shared PostgreSQL container and exhaust max_connections for the tests that run later.
            NpgsqlConnection.ClearAllPools();
        }
    }
}
