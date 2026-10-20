using System.Text;
using System.Text.Json;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Commands;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IndustrialIot.Edge.Tests;

public class CommandExecutorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DecisionOrder_ReasonCodes()
    {
        await using var ctx = await Context.StartAsync();

        // 1. Not a valid command, or too large: no result, no write.
        var malformed = await ctx.Executor.ExecuteAsync(Encoding.UTF8.GetBytes("{ not json"), default);
        Assert.Null(malformed.ResultJson);
        Assert.True(malformed.Acknowledge);
        var oversize = await ctx.Executor.ExecuteAsync(new byte[CommandExecutor.MaxPayloadBytes + 1], default);
        Assert.Null(oversize.ResultJson);

        // 2. Another Edge's command: rejected and not stored.
        var mismatch = ctx.Command("Speed", "5", edgeId: "other-edge");
        await ctx.AssertOutcomeAsync(mismatch, CommandOutcome.Rejected, CommandReasons.EdgeMismatch, stored: false);
        Assert.Null(await ctx.Log.FindAsync(mismatch.CommandId.ToString("D"), default));

        // 8. Expired, 9. issued too far in the future: stored.
        await ctx.AssertOutcomeAsync(ctx.Command("Speed", "5", issued: T0.AddMinutes(-10), lifetime: TimeSpan.FromMinutes(5)),
            CommandOutcome.Expired, null, stored: true);
        await ctx.AssertOutcomeAsync(ctx.Command("Speed", "5", issued: T0.AddMinutes(3)),
            CommandOutcome.Rejected, CommandReasons.ClockSkew, stored: true);

        // 10. The catalog's reasons.
        await ctx.AssertOutcomeAsync(ctx.Command("Nope", "5"), CommandOutcome.Rejected, CommandReasons.UnknownTag, stored: true);
        await ctx.AssertOutcomeAsync(ctx.Command("Sensor", "true"), CommandOutcome.Rejected, CommandReasons.InputAreaNotWritable, stored: true);
        await ctx.AssertOutcomeAsync(ctx.Command("Counter", "5"), CommandOutcome.Rejected, CommandReasons.TagNotWritable, stored: true);
        await ctx.AssertOutcomeAsync(ctx.Command("Speed", "1.5"), CommandOutcome.Rejected, CommandReasons.InvalidValue, stored: true);
        await ctx.AssertOutcomeAsync(ctx.Command("Speed", "1501"), CommandOutcome.Rejected, CommandReasons.ValueOutOfRange, stored: true);

        // 11. Log full of unexpired rows.
        ctx.Log.MaxRows = (int)await ctx.Log.CountAsync(default);
        await ctx.AssertOutcomeAsync(ctx.Command("Speed", "5"), CommandOutcome.Failed, CommandReasons.EdgeStorageError, stored: false);

        Assert.Empty(ctx.Driver.Writes); // none of the above reached the PLC
    }

    [Fact]
    public async Task WriteExceptions_MapToOutcomes()
    {
        await using var ctx = await Context.StartAsync();

        await ctx.AssertOutcomeAsync(ctx.Command("Speed", "5"), CommandOutcome.Applied, null, stored: true);
        var cases = new (Exception Error, CommandOutcome Outcome, string Reason)[]
        {
            (new PlcBusyException("busy"), CommandOutcome.Failed, CommandReasons.PlcBusy), // before its base type
            (new PlcUnavailableException("down"), CommandOutcome.Failed, CommandReasons.PlcUnavailable),
            (new PlcAccessDeniedException("PUT/GET off"), CommandOutcome.Failed, CommandReasons.PlcAccessDenied),
            (new PlcWriteOutcomeUnknownException("no ack"), CommandOutcome.Unknown, CommandReasons.WriteOutcomeUnknown),
            (new InvalidOperationException("unexpected"), CommandOutcome.Unknown, CommandReasons.WriteOutcomeUnknown),
        };

        foreach (var (error, outcome, reason) in cases)
        {
            ctx.Driver.SetWriteFailure("DB1.DBW2", error);
            await ctx.AssertOutcomeAsync(ctx.Command("Speed", "5"), outcome, reason, stored: true);
        }

        Assert.Equal(1 + cases.Length, ctx.Driver.Writes.Count); // each one was attempted once, never retried
        Assert.All(ctx.Driver.Writes, w => Assert.Equal((short)5, w.Value));
    }

    [Fact]
    public async Task Redelivery_RepublishesStoredResult_NoSecondWrite()
    {
        await using var ctx = await Context.StartAsync();
        var command = ctx.Command("Speed", "42");

        var first = await ctx.Executor.ExecuteAsync(ctx.Bytes(command), default);
        ctx.Clock.UtcNow = T0.AddSeconds(5);
        var second = await ctx.Executor.ExecuteAsync(ctx.Bytes(command), default);

        Assert.Equal(CommandOutcome.Applied, Parse(first).Outcome);
        Assert.Equal(first.ResultJson, second.ResultJson); // the identical result, not a new "duplicate"
        Assert.Single(ctx.Driver.Writes);
    }

    [Fact]
    public async Task StartedWithoutResult_IsUnknown_NoWrite()
    {
        await using var ctx = await Context.StartAsync();
        var command = ctx.Command("Speed", "42");
        var id = command.CommandId.ToString("D");

        // As left behind by a process that died during the PLC write.
        await ctx.Log.InsertStartedAsync(id, command.ExpiresAtUtc.ToUnixTimeMilliseconds(), default);
        var result = await ctx.AssertOutcomeAsync(command, CommandOutcome.Unknown, CommandReasons.WriteOutcomeUnknown, stored: true);

        Assert.Empty(ctx.Driver.Writes);
        Assert.True((await ctx.Log.FindAsync(id, default))!.Completed);
        Assert.Equal(result.ResultJson, (await ctx.Executor.ExecuteAsync(ctx.Bytes(command), default)).ResultJson);
    }

    [Fact]
    public async Task ConcurrentRedelivery_WaitsForInFlight()
    {
        await using var ctx = await Context.StartAsync();
        var command = ctx.Command("Speed", "7");
        ctx.Driver.Block();

        var first = ctx.Executor.ExecuteAsync(ctx.Bytes(command), default);
        await ctx.Driver.GateEntered.WaitAsync(TimeSpan.FromSeconds(5));
        var second = ctx.Executor.ExecuteAsync(ctx.Bytes(command), default);
        await Task.Delay(200);
        Assert.False(second.IsCompleted); // waits instead of reporting the half-finished "started" row

        ctx.Driver.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(CommandOutcome.Applied, Parse(results[0]).Outcome);
        Assert.Equal(results[0].ResultJson, results[1].ResultJson);
        Assert.Single(ctx.Driver.Writes);
    }

    [Fact]
    public async Task ClockRollback_RepublishesStoredResult_RejectsNew()
    {
        await using var ctx = await Context.StartAsync();
        var applied = ctx.Command("Speed", "10");
        var firstResult = await ctx.Executor.ExecuteAsync(ctx.Bytes(applied), default);

        ctx.Clock.UtcNow = T0.AddHours(3);
        await ctx.Database.PersistWatermarkAsync(default);
        ctx.Clock.UtcNow = T0.AddHours(1); // two hours behind the highest time seen
        Assert.True(ctx.Database.IsClockRolledBack);

        // A known command still gets its original answer, without a write.
        var redelivered = await ctx.Executor.ExecuteAsync(ctx.Bytes(applied), default);
        Assert.Equal(firstResult.ResultJson, redelivered.ResultJson);

        // A new one cannot be judged fresh against a clock that went backwards.
        await ctx.AssertOutcomeAsync(ctx.Command("Speed", "11", issued: T0.AddHours(1)),
            CommandOutcome.Rejected, CommandReasons.ClockSkew, stored: false);
        Assert.Single(ctx.Driver.Writes);
    }

    [Fact]
    public async Task CompletedStoreFailure_SameResultOnRedelivery()
    {
        await using var ctx = await Context.StartAsync();
        var command = ctx.Command("Speed", "12");
        ctx.Driver.Block();

        var first = ctx.Executor.ExecuteAsync(ctx.Bytes(command), default);
        await ctx.Driver.GateEntered.WaitAsync(TimeSpan.FromSeconds(5)); // "started" is committed, the write is in progress
        await ctx.BreakCommandLogWritesAsync();
        ctx.Driver.Release();

        var unstored = await first;
        Assert.Equal(CommandOutcome.Applied, Parse(unstored).Outcome);
        Assert.False(unstored.Stored);
        Assert.False(unstored.Acknowledge); // leave the message for redelivery

        await ctx.RepairCommandLogWritesAsync();
        var redelivered = await ctx.Executor.ExecuteAsync(ctx.Bytes(command), default);

        Assert.Equal(unstored.ResultJson, redelivered.ResultJson);
        Assert.True(redelivered.Stored);
        Assert.True(redelivered.Acknowledge);
        Assert.Single(ctx.Driver.Writes);
    }

    [Fact]
    public async Task StorageFailure_NoWrite_EdgeStorageError()
    {
        await using var ctx = await Context.StartAsync();
        await ctx.Log.CountAsync(default); // creates the table
        await ctx.BreakCommandLogWritesAsync();

        await ctx.AssertOutcomeAsync(ctx.Command("Speed", "5"), CommandOutcome.Failed, CommandReasons.EdgeStorageError, stored: false);
        Assert.Empty(ctx.Driver.Writes);
    }

    [Fact]
    public async Task LogFull_RejectsWithoutEvictingFreshRows()
    {
        await using var ctx = await Context.StartAsync();

        // A far-future command rejected as clock-skewed is remembered until its own expiry, not less:
        // otherwise the same id could later be executed after "rejected" was reported.
        var skewed = ctx.Command("Speed", "5", issued: T0.AddDays(30), lifetime: TimeSpan.FromHours(1));
        await ctx.AssertOutcomeAsync(skewed, CommandOutcome.Rejected, CommandReasons.ClockSkew, stored: true);
        Assert.Equal(skewed.ExpiresAtUtc.ToUnixTimeMilliseconds(), await ctx.ExpiresUtcMsAsync(skewed));

        var applied = new List<DeviceCommand>();
        for (var i = 0; i < 2; i++)
        {
            applied.Add(ctx.Command("Speed", "5"));
            await ctx.AssertOutcomeAsync(applied[^1], CommandOutcome.Applied, null, stored: true);
        }

        // All results delivered, but every command could still be fresh: nothing may be purged.
        foreach (var command in applied.Append(skewed))
        {
            await ctx.Log.MarkSentAsync(command.CommandId.ToString("D"), default);
        }

        ctx.Log.MaxRows = 3;
        var rejected = ctx.Command("Speed", "6");
        await ctx.AssertOutcomeAsync(rejected, CommandOutcome.Failed, CommandReasons.EdgeStorageError, stored: false);

        Assert.Equal(3, await ctx.Log.CountAsync(default)); // nothing evicted
        Assert.Equal(2, ctx.Driver.Writes.Count); // the rejected command never reached the PLC
    }

    [Fact]
    public async Task UnstoredRejection_NotPublished_DecidedAgain()
    {
        await using var ctx = await Context.StartAsync();
        await ctx.BreakCommandLogWritesAsync();

        // Rejected as clock-skewed, but the decision cannot be stored: nothing is published, nothing is
        // acknowledged — so no answer exists that a later decision could contradict, even after a restart.
        var command = ctx.Command("Speed", "5", issued: T0.AddMinutes(3));
        var first = await ctx.Executor.ExecuteAsync(ctx.Bytes(command), default);
        Assert.Null(first.ResultJson);
        Assert.False(first.Acknowledge);
        Assert.Empty(ctx.Executor.UnstoredResults);

        // Storage recovers and the command has become fresh: the redelivery is decided again and written once.
        await ctx.RepairCommandLogWritesAsync();
        ctx.Clock.UtcNow = T0.AddMinutes(3);
        var redelivered = await ctx.AssertOutcomeAsync(command, CommandOutcome.Applied, null, stored: true);
        Assert.True(redelivered.Acknowledge);
        Assert.Single(ctx.Driver.Writes);
    }

    [Fact]
    public async Task UnstoredUnknown_PublishedAndRemembered()
    {
        await using var ctx = await Context.StartAsync();
        var command = ctx.Command("Speed", "5");
        await ctx.Log.InsertStartedAsync(command.CommandId.ToString("D"), command.ExpiresAtUtc.ToUnixTimeMilliseconds(), default);
        await ctx.BreakCommandLogWritesAsync();

        // Row 6 with a failing store: the "started" row guarantees any later answer is "unknown" too,
        // so it is published now and the identical payload is reused.
        var first = await ctx.AssertOutcomeAsync(command, CommandOutcome.Unknown, CommandReasons.WriteOutcomeUnknown, stored: false);
        Assert.False(first.Acknowledge);

        await ctx.RepairCommandLogWritesAsync();
        var redelivered = await ctx.Executor.ExecuteAsync(ctx.Bytes(command), default);
        Assert.Equal(first.ResultJson, redelivered.ResultJson);
        Assert.True(redelivered.Stored);
        Assert.Empty(ctx.Driver.Writes);
    }

    [Fact]
    public async Task Purge_OnlyExpiredSentRows_WatermarkFirst()
    {
        await using var ctx = await Context.StartAsync();
        var cutoff = T0 - EdgeDatabase.RollbackTolerance; // rows expiring before now − 2 min may go
        await ctx.StoreSentAsync("expired-sent", cutoff.AddMilliseconds(-1), sent: true);
        await ctx.StoreSentAsync("at-boundary", cutoff, sent: true);
        await ctx.StoreSentAsync("expired-unsent", cutoff.AddHours(-1), sent: false);

        Assert.Equal(1, await ctx.Log.PurgeAsync(default));
        Assert.Null(await ctx.Log.FindAsync("expired-sent", default));
        Assert.NotNull(await ctx.Log.FindAsync("at-boundary", default));
        Assert.NotNull(await ctx.Log.FindAsync("expired-unsent", default)); // its result still has to go out

        // During a clock rollback nothing is purged.
        await ctx.StoreSentAsync("old-sent", cutoff.AddHours(-1), sent: true);
        ctx.Clock.UtcNow = T0.AddHours(3);
        await ctx.Database.PersistWatermarkAsync(default);
        ctx.Clock.UtcNow = T0.AddHours(1);
        Assert.Equal(0, await ctx.Log.PurgeAsync(default));
        Assert.NotNull(await ctx.Log.FindAsync("old-sent", default));

        // The watermark is committed before deleting: even a purge whose delete fails has persisted it.
        ctx.Clock.UtcNow = T0.AddHours(5);
        await ctx.ExecuteSqlAsync("CREATE TRIGGER test_fail_delete BEFORE DELETE ON command_log BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => ctx.Log.PurgeAsync(default));
        Assert.True(await ctx.PersistedWatermarkAsync() >= T0.AddHours(5).ToUnixTimeMilliseconds());
    }

    private static CommandResult Parse(CommandDisposition disposition) =>
        ContractJson.Deserialize<CommandResult>(disposition.ResultJson!);

    private sealed class Context : IAsyncDisposable
    {
        private readonly EdgeTestHost _edge;

        private Context(EdgeTestHost edge, FakePlcDriver driver, MutableClock clock)
        {
            _edge = edge;
            Driver = driver;
            Clock = clock;
        }

        public FakePlcDriver Driver { get; }

        public MutableClock Clock { get; }

        public CommandExecutor Executor => _edge.Services.GetRequiredService<CommandExecutor>();

        public CommandLog Log => _edge.Services.GetRequiredService<CommandLog>();

        public EdgeDatabase Database => _edge.Services.GetRequiredService<EdgeDatabase>();

        public static async Task<Context> StartAsync()
        {
            var driver = new FakePlcDriver();
            var clock = new MutableClock(T0);
            var edge = EdgeTestHost.Create(
                new Dictionary<string, string?>
                {
                    ["Edge:Tags:0:TagId"] = "Speed",
                    ["Edge:Tags:0:Address"] = "DB1.DBW2",
                    ["Edge:Tags:0:DataType"] = "Int",
                    ["Edge:Tags:0:Writable"] = "true",
                    ["Edge:Tags:0:Min"] = "0",
                    ["Edge:Tags:0:Max"] = "1500",
                    ["Edge:Tags:1:TagId"] = "Counter",
                    ["Edge:Tags:1:Address"] = "DB1.DBD8",
                    ["Edge:Tags:1:DataType"] = "DInt",
                    ["Edge:Tags:2:TagId"] = "Sensor",
                    ["Edge:Tags:2:Address"] = "I0.0",
                    ["Edge:Tags:2:DataType"] = "Bool",
                },
                services =>
                {
                    services.AddSingleton<IPlcDriver>(driver);
                    services.AddSingleton<TimeProvider>(clock);
                });
            await edge.Host.StartAsync();
            return new Context(edge, driver, clock);
        }

        public DeviceCommand Command(
            string tagId, string valueJson, string edgeId = "test-edge", DateTimeOffset? issued = null, TimeSpan? lifetime = null)
        {
            var issuedAt = issued ?? Clock.UtcNow;
            return new DeviceCommand
            {
                SchemaVersion = 1,
                CommandId = Guid.NewGuid(),
                EdgeId = edgeId,
                TagId = tagId,
                TargetValue = JsonDocument.Parse(valueJson).RootElement.Clone(),
                IssuedAtUtc = issuedAt,
                ExpiresAtUtc = issuedAt + (lifetime ?? TimeSpan.FromMinutes(1)),
                IssuedBy = "test-operator",
            };
        }

        public byte[] Bytes(DeviceCommand command) => Encoding.UTF8.GetBytes(ContractJson.Serialize(command));

        public async Task<CommandDisposition> AssertOutcomeAsync(DeviceCommand command, CommandOutcome outcome, string? reason, bool stored)
        {
            var disposition = await Executor.ExecuteAsync(Bytes(command), default);
            var result = Parse(disposition);

            Assert.Equal(command.CommandId, result.CommandId);
            Assert.Equal("test-edge", result.EdgeId);
            Assert.Equal(outcome, result.Outcome);
            Assert.Equal(reason, result.Reason);
            Assert.Equal(stored, disposition.Stored);
            return disposition;
        }

        public async Task<long> ExpiresUtcMsAsync(DeviceCommand command)
        {
            await using var connection = await Database.OpenConnectionAsync(default);
            await using var sql = connection.CreateCommand();
            sql.CommandText = "SELECT expires_utc_ms FROM command_log WHERE command_id = $id;";
            sql.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            return (long)(await sql.ExecuteScalarAsync())!;
        }

        /// <summary>Makes every insert/update on the command log fail immediately (like a full disk), reads still work.</summary>
        public Task BreakCommandLogWritesAsync() => ExecuteSqlAsync("""
            CREATE TRIGGER IF NOT EXISTS test_fail_insert BEFORE INSERT ON command_log BEGIN SELECT RAISE(ABORT, 'simulated disk failure'); END;
            CREATE TRIGGER IF NOT EXISTS test_fail_update BEFORE UPDATE ON command_log BEGIN SELECT RAISE(ABORT, 'simulated disk failure'); END;
            """);

        public Task RepairCommandLogWritesAsync() => ExecuteSqlAsync("""
            DROP TRIGGER IF EXISTS test_fail_insert;
            DROP TRIGGER IF EXISTS test_fail_update;
            """);

        public ValueTask DisposeAsync() => _edge.DisposeAsync();

        public async Task StoreSentAsync(string commandId, DateTimeOffset expires, bool sent)
        {
            await Log.CompleteAsync(commandId, "{}", expires.ToUnixTimeMilliseconds(), default);
            if (sent)
            {
                await Log.MarkSentAsync(commandId, default);
            }
        }

        public async Task<long> PersistedWatermarkAsync()
        {
            await using var connection = await Database.OpenConnectionAsync(default);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = 'last_seen_utc_ms';";
            return long.Parse((string)(await command.ExecuteScalarAsync())!, System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task ExecuteSqlAsync(string sql)
        {
            await Log.CountAsync(default); // make sure the table exists
            await using var connection = await Database.OpenConnectionAsync(default);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>A clock tests can move backwards (FakeTimeProvider only moves forward).</summary>
    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
