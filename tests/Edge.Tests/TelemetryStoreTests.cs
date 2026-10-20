using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Tests;

public class TelemetryStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AppendBatch_AssignsGapFreeSequence_AndSurvivesReopen()
    {
        var directory = NewTempDirectory();
        var overrides = new Dictionary<string, string?> { ["Edge:Buffer:Path"] = Path.Combine(directory, "edge.db") };
        try
        {
            Guid streamId;
            await using (var first = await StartAsync(new MutableClock(T0), overrides))
            {
                var stored = await Store(first).AppendBatchAsync([Sample("A", 1), Sample("B", 2), Sample("C", 3)], default);

                Assert.Equal([0L, 1L, 2L], stored.Select(p => p.Sequence));
                streamId = stored[0].StreamId;
                Assert.All(stored, p => Assert.Equal(streamId, p.StreamId));
                Assert.All(stored, p => Assert.Equal("test-edge", p.EdgeId));

                var rows = await Store(first).ReadBatchAsync(10, default);
                Assert.Equal(stored, rows.Select(r => ContractJson.Deserialize<TelemetryPayload>(r.Payload)));
            }

            await using var reopened = await StartAsync(new MutableClock(T0.AddMinutes(5)), overrides);
            var next = await Store(reopened).AppendBatchAsync([Sample("D", 4)], default);

            Assert.Equal(3, next[0].Sequence);
            Assert.Equal(streamId, next[0].StreamId);
        }
        finally
        {
            EdgeTestHost.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task NewFile_StartsNewStream()
    {
        await using var a = await StartAsync(new MutableClock(T0));
        await using var b = await StartAsync(new MutableClock(T0));

        var fromA = await Store(a).AppendBatchAsync([Sample("A", 1)], default);
        var fromB = await Store(b).AppendBatchAsync([Sample("A", 1)], default);

        Assert.Equal(0, fromA[0].Sequence);
        Assert.Equal(0, fromB[0].Sequence);
        Assert.NotEqual(fromA[0].StreamId, fromB[0].StreamId);
    }

    [Fact]
    public async Task FailedBatch_InsertsNothing()
    {
        await using var edge = await StartAsync(new MutableClock(T0));
        var invalid = new TelemetrySample("B", null, TelemetryQuality.Good, T0); // good quality needs a value

        await Assert.ThrowsAsync<ContractValidationException>(() => Store(edge).AppendBatchAsync([Sample("A", 1), invalid], default));

        Assert.Empty(await Store(edge).ReadBatchAsync(10, default));
        Assert.Equal(0, await Store(edge).CountAsync(default));
        var next = await Store(edge).AppendBatchAsync([Sample("A", 1)], default);
        Assert.Equal(0, next[0].Sequence); // the failed batch did not consume sequence numbers
    }

    [Fact]
    public async Task Purge_DropsOldestBeyondLimits()
    {
        var clock = new MutableClock(T0);
        var logs = new LogCapture();
        await using var edge = await StartAsync(clock, new Dictionary<string, string?> { ["Edge:Buffer:MaxRows"] = "1000" }, logs);
        var store = Store(edge);

        await store.AppendBatchAsync(Enumerable.Range(0, 1000).Select(i => Sample("A", i)).ToList(), default);
        for (var i = 0; i < 5; i++)
        {
            await store.AppendBatchAsync([Sample("A", i)], default);
        }

        var rows = await store.ReadBatchAsync(2000, default);
        Assert.Equal(1000, rows.Count);
        Assert.Equal(5, rows[0].Sequence);
        Assert.Equal(1004, rows[^1].Sequence);
        Assert.Equal(5, logs.DroppedCounts().Sum());

        // Age limit: everything is now older than MaxAgeHours (168 h).
        clock.UtcNow = T0.AddHours(169);
        Assert.Equal(1000, await store.PurgeAsync(default));
        Assert.Empty(await store.ReadBatchAsync(10, default));
        Assert.Equal(1005, logs.DroppedCounts().Sum());
    }

    [Fact]
    public async Task Purge_SkippedWhileClockRolledBack()
    {
        var clock = new MutableClock(T0);
        var logs = new LogCapture();
        await using var edge = await StartAsync(clock, logs: logs);
        var store = Store(edge);
        var database = edge.Services.GetRequiredService<EdgeDatabase>();
        await store.AppendBatchAsync(Enumerable.Range(0, 10).Select(i => Sample("A", i)).ToList(), default);

        clock.UtcNow = T0.AddHours(200);
        await database.PersistWatermarkAsync(default);
        clock.UtcNow = T0.AddHours(199); // one hour behind the watermark; rows are older than 168 h

        Assert.Equal(0, await store.PurgeAsync(default));
        Assert.Equal(0, await store.PurgeAsync(default));

        Assert.True(database.IsClockRolledBack);
        Assert.Equal(10, (await store.ReadBatchAsync(100, default)).Count);
        Assert.Equal(1, logs.Count(m => m.Contains("clock is behind", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Purge_PersistsWatermarkFirst()
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "edge.db");
        try
        {
            var clock = new MutableClock(T0);
            await using (var edge = await StartAsync(clock, new Dictionary<string, string?> { ["Edge:Buffer:Path"] = path }))
            {
                clock.UtcNow = T0.AddHours(10);
                await Store(edge).PurgeAsync(default);

                // Read the file from a separate connection, as a restarted process would.
                await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM meta WHERE key = 'last_seen_utc_ms';";
                var persisted = long.Parse((string)(await command.ExecuteScalarAsync())!, CultureInfo.InvariantCulture);

                Assert.True(persisted >= T0.AddHours(10).ToUnixTimeMilliseconds());
            }
        }
        finally
        {
            EdgeTestHost.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task AcknowledgeRange_RemovesOnlyThatRangeOfThisStream()
    {
        await using var edge = await StartAsync(new MutableClock(T0));
        var store = Store(edge);
        var stored = await store.AppendBatchAsync(Enumerable.Range(0, 6).Select(i => Sample("A", i)).ToList(), default);
        var stream = stored[0].StreamId;

        Assert.Equal(2, await store.AcknowledgeRangeAsync(stream, 1, 2, default));
        Assert.Equal(0, await store.AcknowledgeRangeAsync(Guid.NewGuid(), 0, 5, default)); // another stream: nothing
        Assert.Equal(0, await store.AcknowledgeRangeAsync(stream, 1, 2, default));         // already gone
        Assert.Equal(1, await store.AcknowledgeRangeAsync(stream, 5, 99, default));        // beyond the queue: only 5

        Assert.Equal([0L, 3L, 4L], (await store.ReadBatchAfterAsync(-1, 10, default)).Select(r => r.Sequence));

        // Reading never removes anything: only a confirmation does.
        Assert.Equal([3L, 4L], (await store.ReadBatchAfterAsync(0, 10, default)).Select(r => r.Sequence));
        Assert.Equal([0L, 3L, 4L], (await store.ReadBatchAfterAsync(-1, 10, default)).Select(r => r.Sequence));
    }

    [Fact]
    public async Task Counts_ExactWithGaps()
    {
        await using var edge = await StartAsync(new MutableClock(T0));
        var store = Store(edge);
        var stored = await store.AppendBatchAsync(Enumerable.Range(0, 10).Select(i => Sample("A", i)).ToList(), default);
        await store.AcknowledgeRangeAsync(stored[0].StreamId, 2, 3, default);
        await store.AcknowledgeRangeAsync(stored[0].StreamId, 7, 7, default);

        // next_sequence − oldest would say 10; the queue holds 7.
        Assert.Equal(7, await store.CountAsync(default));
        Assert.Equal(3, await store.CountUpToAsync(4, default)); // 0, 1, 4
        Assert.Equal(0, await store.CountUpToAsync(-1, default));
        Assert.Equal(7, await store.CountUpToAsync(99, default));
    }

    [Fact]
    public async Task Pragmas_AndFileMode()
    {
        await using var edge = await StartAsync(new MutableClock(T0));
        var database = edge.Services.GetRequiredService<EdgeDatabase>();

        await using var connection = await database.OpenConnectionAsync(default);
        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(2L, await ScalarAsync(connection, "PRAGMA synchronous;")); // FULL
        Assert.Equal(5000L, await ScalarAsync(connection, "PRAGMA busy_timeout;"));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(database.FilePath));
        }
    }

    [Fact]
    public async Task AgePurge_UsesCreatedIndex()
    {
        await using var edge = await StartAsync(new MutableClock(T0));
        var database = edge.Services.GetRequiredService<EdgeDatabase>();

        // Ask the bundled SQLite how it would run the age-purge chunk query.
        await using var connection = await database.OpenConnectionAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + TelemetryStore.ChunkRangeSql(TelemetryStore.AgeCondition, TelemetryStore.AgeOrder);
        command.Parameters.AddWithValue("$limit", 0L);

        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.Contains(plan, step => step.Contains("telemetry_created", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN telemetry", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Edge:Buffer:MaxRows", "10")]
    [InlineData("Edge:Buffer:MaxAgeHours", "0")]
    [InlineData("Edge:Buffer:Path", "")]
    public async Task InvalidBufferConfig_FailsAtStart(string key, string value)
    {
        await using var edge = EdgeTestHost.Create(new Dictionary<string, string?> { [key] = value });

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => edge.Host.StartAsync());
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotASqliteFile_FailsAtStart()
    {
        var directory = NewTempDirectory();
        var path = Path.Combine(directory, "edge.db");
        try
        {
            await File.WriteAllTextAsync(path, "this is not a SQLite database, just some text that is long enough to have a header");
            await using var edge = EdgeTestHost.Create(new Dictionary<string, string?> { ["Edge:Buffer:Path"] = path });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => edge.Host.StartAsync());
            Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            EdgeTestHost.DeleteDirectory(directory);
        }
    }

    private static async Task<EdgeTestHost> StartAsync(
        MutableClock clock, IReadOnlyDictionary<string, string?>? overrides = null, LogCapture? logs = null)
    {
        var edge = EdgeTestHost.Create(overrides, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        });
        await edge.Host.StartAsync();
        return edge;
    }

    private static TelemetryStore Store(EdgeTestHost edge) => edge.Services.GetRequiredService<TelemetryStore>();

    private static TelemetrySample Sample(string tagId, int value) =>
        new(tagId, JsonSerializer.SerializeToElement(value), TelemetryQuality.Good, T0);

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>A clock tests can move backwards (FakeTimeProvider only moves forward).</summary>
    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public int Count(Func<string, bool> predicate) => _messages.Count(predicate);

        public IEnumerable<int> DroppedCounts() => _messages
            .Where(m => m.StartsWith("Telemetry buffer dropped ", StringComparison.Ordinal))
            .Select(m => int.Parse(m.Split(' ')[3], CultureInfo.InvariantCulture));

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
}
