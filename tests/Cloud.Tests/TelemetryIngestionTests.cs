using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Ingestion;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Cloud.Tests.Infrastructure;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Cloud.Tests;

[Collection(CloudCollection.Name)]
public class TelemetryIngestionTests(PostgresFixture database)
{
    private static readonly Guid Stream = Guid.NewGuid();

    [Fact]
    public async Task Duplicates_StoredOnce()
    {
        await using var ctx = await StartAsync(database);
        var now = DateTimeOffset.UtcNow;

        // At-least-once delivery from the Edge: every sample arrives twice.
        for (var round = 0; round < 2; round++)
        {
            for (var sequence = 1; sequence <= 3; sequence++)
            {
                await ctx.PublishAsync("edge-a", Sample("edge-a", sequence, "t1", sequence, now.AddSeconds(sequence)));
            }
        }

        // Messages are handled in order, so once the marker is stored every duplicate has been handled.
        await ctx.PublishAsync("edge-a", Sample("edge-a", 99, "marker", 0, now));
        await WaitUntilAsync(async () => await ctx.CountTelemetryAsync() == 4, "marker stored");

        Assert.Equal([1L, 2L, 3L, 99L], await ctx.QueryAsync(db => db.Telemetry.OrderBy(t => t.Sequence).Select(t => t.Sequence).ToListAsync()));
    }

    [Fact]
    public async Task ExtremeNumber_StoredNormalized()
    {
        await using var ctx = await StartAsync(database);
        var now = DateTimeOffset.UtcNow;

        // Valid by contract (underflows to a finite 0), but PostgreSQL jsonb cannot hold the literal text.
        var tiny = Sample("edge-a", 1, "t1", 1, now).Replace("\"value\":1", "\"value\":1e-20000", StringComparison.Ordinal);
        Assert.Contains("1e-20000", tiny, StringComparison.Ordinal);
        await ctx.PublishAsync("edge-a", tiny);
        await ctx.PublishAsync("edge-a", Sample("edge-a", 2, "marker", 0, now));
        await WaitUntilAsync(async () => await ctx.CountTelemetryAsync() == 2, "both stored without blocking the queue");

        Assert.Equal("0", await ctx.QueryAsync(db => db.Telemetry.Where(t => t.Sequence == 1).Select(t => t.Value).SingleAsync()));
    }

    [Fact]
    public async Task LatestValue_OnlyNewer()
    {
        await using var ctx = await StartAsync(database);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        await ctx.PublishAsync("edge-a", Sample("edge-a", 1, "t1", 10, now));
        await ctx.PublishAsync("edge-a", Sample("edge-a", 2, "t1", 20, now.AddSeconds(10)));
        await ctx.PublishAsync("edge-a", Sample("edge-a", 3, "t1", 30, now.AddSeconds(5))); // older, arrives last (replay)
        await WaitUntilAsync(async () => await ctx.CountTelemetryAsync() == 3, "three samples stored");

        var latest = await ctx.QueryAsync(db => db.LatestValues.SingleAsync(l => l.EdgeId == "edge-a" && l.TagId == "t1"));
        Assert.Equal("20", latest.Value);
        Assert.Equal(2, latest.Sequence);
        Assert.Equal(now.AddSeconds(10).ToUnixTimeMilliseconds(), latest.TimestampUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Spoofed_Invalid_FutureDropped()
    {
        var logs = new CapturingLoggerProvider();
        await using var ctx = await StartAsync(database, configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        var now = DateTimeOffset.UtcNow;

        await ctx.PublishAsync("edge-a", Sample("edge-b", 1, "t1", 1, now));                 // payload claims another Edge
        await ctx.PublishAsync("edge-x", Sample("edge-x", 2, "t1", 1, now));                 // Edge not allow-listed
        await ctx.PublishAsync("edge-a", "{\"schemaVersion\":1,");                             // malformed JSON
        await ctx.PublishAsync("edge-a", new string(' ', 17 * 1024) + Sample("edge-a", 3, "t1", 1, now)); // > 16 KiB
        await ctx.PublishAsync("edge-a", Sample("edge-a", 4, "t1", 1, now.AddMinutes(10)));  // 10 min in the future

        await ctx.PublishAsync("edge-a", Sample("edge-a", 100, "marker", 0, now));
        await WaitUntilAsync(async () => await ctx.CountTelemetryAsync() == 1, "first marker stored");
        var dropsBefore = logs.Count("Dropping telemetry");

        // Dropped messages were acknowledged: a new session gets none of them again.
        var attempts = ctx.Broker.ConnectAttempts.Count;
        await ctx.Connection.ForceReconnectAsync();
        await WaitUntilAsync(() => Task.FromResult(ctx.Broker.ConnectAttempts.Count > attempts && ctx.Connection.IsConnected), "reconnected");
        await ctx.PublishAsync("edge-a", Sample("edge-a", 101, "marker", 0, now));
        await WaitUntilAsync(async () => await ctx.CountTelemetryAsync() == 2, "second marker stored");

        Assert.True(dropsBefore >= 4, $"expected the invalid messages to be dropped, saw {dropsBefore}"); // the oversized one may be discarded by the broker
        Assert.Equal(dropsBefore, logs.Count("Dropping telemetry"));
        Assert.Equal([100L, 101L], await ctx.QueryAsync(db => db.Telemetry.OrderBy(t => t.Sequence).Select(t => t.Sequence).ToListAsync()));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("schemaVersion", StringComparison.Ordinal)); // no payload in logs
    }

    [Fact]
    public async Task DatabaseFailure_RetriedInPlace()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var ctx = await StartAsync(database, clock);
        var attempts = ctx.Broker.ConnectAttempts.Count;

        ctx.Host.DatabaseFaults.Enabled = true;
        var commands = ctx.Host.DatabaseFaults.CommandCount;
        await ctx.PublishAsync("edge-a", Sample("edge-a", 1, "t1", 1, clock.GetUtcNow()));
        await WaitUntilAsync(() => Task.FromResult(ctx.Host.DatabaseFaults.CommandCount > commands), "first store attempt");

        // Two seconds of outage: retried on the back-off clock, nothing stored yet.
        await StepClockAsync(clock, TimeSpan.FromSeconds(2));
        Assert.Equal(0, await ctx.CountTelemetryAsyncIgnoringFaults());

        ctx.Host.DatabaseFaults.Enabled = false;
        await StepUntilAsync(clock, async () => await ctx.CountTelemetryAsync() == 1, "stored after the outage");

        // Recovered in place: same session, no reconnect needed.
        Assert.Equal(attempts, ctx.Broker.ConnectAttempts.Count);
        Assert.True(ctx.Connection.IsConnected);
    }

    [Fact]
    public async Task DatabaseFailure_ReconnectRedelivers()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var ctx = await StartAsync(database, clock);
        var attempts = ctx.Broker.ConnectAttempts.Count;

        ctx.Host.DatabaseFaults.Enabled = true;
        var commands = ctx.Host.DatabaseFaults.CommandCount;
        await ctx.PublishAsync("edge-a", Sample("edge-a", 1, "t1", 1, clock.GetUtcNow()));
        await WaitUntilAsync(() => Task.FromResult(ctx.Host.DatabaseFaults.CommandCount > commands), "first store attempt");

        // After 30 s of failures the Cloud gives up on the message unacknowledged and drops the session connection.
        await StepUntilAsync(clock, () => Task.FromResult(ctx.Broker.ConnectAttempts.Count > attempts), "reconnect after the retry budget");
        Assert.True(clock.GetUtcNow() - ctx.StartedAt >= DatabaseRetry.Budget);

        // Only the broker can bring the message back now.
        ctx.Host.DatabaseFaults.Enabled = false;
        await StepUntilAsync(clock, async () => await ctx.CountTelemetryAsync() == 1, "redelivered message stored");
        Assert.Equal([1L], await ctx.QueryAsync(db => db.Telemetry.Select(t => t.Sequence).ToListAsync()));
    }

    [Fact]
    public async Task Retention_DeletesOldRows()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var ctx = await StartAsync(database, clock);
        var job = ctx.Host.Services.GetServices<IHostedService>().OfType<RetentionJob>().Single();

        await ctx.PublishAsync("edge-a", Sample("edge-a", 1, "old", 1, clock.GetUtcNow()));
        await WaitUntilAsync(async () => await ctx.CountTelemetryAsync() == 1, "old sample stored");

        // 89 days later: one hourly run happens, nothing is old enough yet.
        await WaitUntilAsync(() => Task.FromResult(job.WaitsArmed >= 1), "job waiting");
        clock.Advance(TimeSpan.FromDays(89));
        await WaitUntilAsync(() => Task.FromResult(job.WaitsArmed >= 2), "first run done");
        Assert.Equal(1, await ctx.CountTelemetryAsync());

        await ctx.PublishAsync("edge-a", Sample("edge-a", 2, "new", 2, clock.GetUtcNow()));
        await WaitUntilAsync(async () => await ctx.CountTelemetryAsync() == 2, "new sample stored");

        // Past 90 days for the first row only.
        clock.Advance(TimeSpan.FromDays(2)); // a day of margin either way of the 90-day cutoff
        await WaitUntilAsync(() => Task.FromResult(job.WaitsArmed >= 3), "second run done");

        Assert.Equal([2L], await ctx.QueryAsync(db => db.Telemetry.Select(t => t.Sequence).ToListAsync()));
        Assert.Equal(["new", "old"], await ctx.QueryAsync(db => db.LatestValues.OrderBy(l => l.TagId).Select(l => l.TagId).ToListAsync()));
    }

    [Fact]
    public async Task Readiness_TracksMqtt()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var ctx = await StartAsync(database, clock, new Dictionary<string, string?> { ["Cloud:Health:RefreshSeconds"] = "7" });
        var monitor = ctx.Host.Services.GetServices<IHostedService>().OfType<Health.ReadinessMonitor>().Single();
        var refresh = TimeSpan.FromSeconds(7);

        await WaitUntilAsync(async () => (await ctx.Client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK, "ready");

        await WaitUntilAsync(() => Task.FromResult(monitor.WaitsArmed >= 1), "monitor waiting");
        await ctx.Broker.StopServerAsync();
        await WaitUntilAsync(() => Task.FromResult(!ctx.Connection.IsConnected), "connection lost");
        var armed = monitor.WaitsArmed;
        clock.Advance(refresh);
        await WaitUntilAsync(() => Task.FromResult(monitor.WaitsArmed > armed), "refreshed");

        var down = await ctx.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        var checks = JsonDocument.Parse(await down.Content.ReadAsStringAsync()).RootElement.GetProperty("checks");
        Assert.False(checks.GetProperty("mqtt").GetBoolean());
        Assert.True(checks.GetProperty("database").GetBoolean());

        // Back after the reconnect back-off (on the fake clock) and the next refresh. Stepping by whole refresh
        // periods: a refresh may already be running when the connection returns, so one Advance could land
        // before the monitor armed its next timer.
        await ctx.Broker.StartServerAsync();
        await StepUntilAsync(clock, () => Task.FromResult(ctx.Connection.IsConnected), "reconnected", TimeSpan.FromMilliseconds(500));
        await StepUntilAsync(clock, async () => (await ctx.Client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK, "ready again", refresh);
    }

    [Theory]
    [InlineData("Cloud:Mqtt:Host", "", "Cloud:Mqtt:Host")]
    [InlineData("Cloud:Mqtt:Port", "0", "Cloud:Mqtt:Port")]
    [InlineData("Cloud:Mqtt:ClientId", "a b", "Cloud:Mqtt:ClientId")]
    [InlineData("Cloud:Mqtt:Password", "", "Cloud:Mqtt:Password")]
    public async Task InvalidMqttConfig_FailsAtStart(string key, string value, string expectedKey)
    {
        await using var host = new CloudTestHost(database, new Dictionary<string, string?> { [key] = value });

        var error = Record.Exception(() => host.CreateClient());

        var validation = Find<OptionsValidationException>(error);
        Assert.NotNull(validation);
        Assert.Contains(expectedKey, validation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Broker.Password, validation.ToString(), StringComparison.Ordinal);
    }

    private static string Sample(string edgeId, long sequence, string tagId, double value, DateTimeOffset timestamp) =>
        ContractJson.Serialize(new TelemetryPayload
        {
            SchemaVersion = ContractJson.SchemaVersion,
            EdgeId = edgeId,
            StreamId = Stream,
            Sequence = sequence,
            TagId = tagId,
            Value = JsonSerializer.SerializeToElement(value),
            Quality = TelemetryQuality.Good,
            TimestampUtc = timestamp.ToUniversalTime(),
        });

    private static async Task<IngestionContext> StartAsync(
        PostgresFixture database,
        FakeTimeProvider? clock = null,
        Dictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = new CloudTestHost(database, overrides, services =>
        {
            if (clock is not null)
            {
                services.AddSingleton<TimeProvider>(clock);
            }

            configureServices?.Invoke(services);
        });
        var client = host.CreateClient();
        var connection = host.Services.GetRequiredService<CloudMqttConnection>();

        // Subscribed before anything is published: an injected message without a subscriber is lost.
        await connection.FirstAttemptCompleted.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(connection.IsConnected);
        return new IngestionContext(host, client, connection, clock?.GetUtcNow() ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Moves the fake clock in small steps, giving background loops time to arm their timers.</summary>
    private static async Task StepClockAsync(FakeTimeProvider clock, TimeSpan total)
    {
        var step = TimeSpan.FromMilliseconds(250);
        for (var moved = TimeSpan.Zero; moved < total; moved += step)
        {
            clock.Advance(step);
            await Task.Delay(20);
        }
    }

    private static async Task StepUntilAsync(FakeTimeProvider clock, Func<Task<bool>> condition, string what, TimeSpan? step = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), $"timed out waiting for: {what}");
            clock.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(30);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), $"timed out waiting for: {what}");
            await Task.Delay(50);
        }
    }

    private static T? Find<T>(Exception? error)
        where T : Exception
    {
        for (var pending = new Stack<Exception>(error is null ? [] : [error]); pending.Count > 0;)
        {
            var current = pending.Pop();
            if (current is T typed)
            {
                return typed;
            }

            foreach (var inner in current is AggregateException a ? a.InnerExceptions : current.InnerException is { } i ? [i] : [])
            {
                pending.Push(inner);
            }
        }

        return null;
    }

    private sealed record IngestionContext(CloudTestHost Host, HttpClient Client, CloudMqttConnection Connection, DateTimeOffset StartedAt)
        : IAsyncDisposable
    {
        public Edge.Tests.Fakes.TestMqttBroker Broker => Host.Broker;

        public Task PublishAsync(string edgeId, string payload) => Host.Broker.PublishAsync($"iiot/{edgeId}/telemetry", payload);

        public Task<int> CountTelemetryAsync() => QueryAsync(db => db.Telemetry.CountAsync());

        /// <summary>Counts through a context without the fault interceptor, so the check works during an outage.</summary>
        public async Task<int> CountTelemetryAsyncIgnoringFaults()
        {
            var options = new DbContextOptionsBuilder<CloudDbContext>()
                .UseNpgsql(Host.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString)
                .Options;
            await using var db = new CloudDbContext(options);
            return await db.Telemetry.CountAsync();
        }

        public async Task<T> QueryAsync<T>(Func<CloudDbContext, Task<T>> query)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            return await query(scope.ServiceProvider.GetRequiredService<CloudDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

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
                messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}
