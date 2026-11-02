using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Ingestion;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Cloud.Tests.Infrastructure;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MQTTnet.Protocol;

namespace IndustrialIot.Cloud.Tests;

/// <summary>
/// Inventory parts from <c>iiot/{edgeId}/inventory</c>: assembled per (edge, boot, round) in one bounded buffer per
/// Edge, checked as a round, stored as one row per Edge when complete (newest wins, at most once per 5 s), without
/// ever forcing a reconnect. A telemetry marker published after the parts proves they were handled (one message at a time).
/// </summary>
[Collection(CloudCollection.Name)]
public class InventoryIngestionTests(PostgresFixture database)
{
    private static readonly DateTimeOffset Sent = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Subscriptions_InventoryAtQoS0_TelemetryAndResultsStillAtQoS1()
    {
        var requested = new ConcurrentDictionary<string, MqttQualityOfServiceLevel>();
        await using var ctx = await StartAsync(database, beforeBuild: host => host.Broker.Server.InterceptingSubscriptionAsync += e =>
        {
            requested[e.TopicFilter.Topic] = e.TopicFilter.QualityOfServiceLevel;
            return Task.CompletedTask;
        });

        Assert.Equal(MqttQualityOfServiceLevel.AtMostOnce, requested[InventoryIngestor.TopicFilter]);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, requested[TelemetryIngestor.TopicFilter]);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, requested["iiot/+/command-results"]);
    }

    [Fact]
    public async Task TelemetryGrantedBelowQoS1_StillFailsTheConnection()
    {
        var host = new CloudTestHost(database);
        host.Broker.Server.InterceptingSubscriptionAsync += e =>
        {
            if (e.TopicFilter.Topic == TelemetryIngestor.TopicFilter)
            {
                e.Response.ReasonCode = MQTTnet.Protocol.MqttSubscribeReasonCode.GrantedQoS0;
            }

            return Task.CompletedTask;
        };
        await using (host)
        {
            using var client = host.CreateClient();
            var connection = host.Services.GetRequiredService<CloudMqttConnection>();
            await connection.FirstAttemptCompleted.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.False(connection.IsConnected);
        }
    }

    [Fact]
    public async Task CompleteRound_IsStoredAsOneDocument_InEdgeOrder()
    {
        var clock = new FakeTimeProvider(Sent.AddMinutes(1));
        await using var ctx = await StartAsync(database, clock);
        var boot = Guid.NewGuid();

        foreach (var part in Round(boot, 0))
        {
            await ctx.PublishAsync("edge-a", part);
        }

        var row = await ctx.WaitForRowAsync("edge-a", r => r.Round == 0);
        Assert.Equal(boot, row.BootId);
        Assert.Equal(clock.GetUtcNow(), row.ReceivedUtc);
        var document = Document(row);
        Assert.True(document.CommandsEnabled);
        Assert.Equal(["Line", "Press"], document.Devices.Select(d => d.Status.Name));
        Assert.Equal(["Line.Speed", "Line.Batch"], document.Devices[0].Tags.Select(t => t.TagId));
        Assert.Equal(["Press.Count"], document.Devices[1].Tags.Select(t => t.TagId));
        Assert.Equal("Belt speed", document.Devices[0].Tags[0].Description);
    }

    [Fact]
    public async Task PartsOutOfOrder_AreAssembled()
    {
        await using var ctx = await StartAsync(database);
        var parts = Round(Guid.NewGuid(), 0);

        foreach (var index in new[] { 2, 0, 1 })
        {
            await ctx.PublishAsync("edge-a", parts[index]);
        }

        Assert.Equal(["Line", "Press"], Document(await ctx.WaitForRowAsync("edge-a", r => true)).Devices.Select(d => d.Status.Name));
    }

    [Fact]
    public async Task DuplicateIndex_MismatchedCount_AndMissingPart_StoreNothing_UntilTheNextRoundCompletes()
    {
        await using var ctx = await StartAsync(database);
        var boot = Guid.NewGuid();
        var round0 = Round(boot, 0);

        await ctx.PublishAsync("edge-a", round0[0]);
        await ctx.PublishAsync("edge-a", round0[1]);
        await ctx.PublishAsync("edge-a", round0[1]); // redelivered: 0, 1, 1 of 3 is not complete
        await ctx.PublishAsync("edge-a", Part(boot, 0, part: 2, partCount: 4, tags: Tags("Press", "Press.Count"))); // other partCount
        await ctx.MarkerAsync();
        Assert.Null(await ctx.RowAsync("edge-a"));
        Assert.Equal(2, ctx.Ingestor.BufferOf("edge-a")!.Value.Parts);

        // The next round of the same boot replaces the incomplete buffer and is stored.
        foreach (var part in Round(boot, 1))
        {
            await ctx.PublishAsync("edge-a", part);
        }

        Assert.Equal(1, (await ctx.WaitForRowAsync("edge-a", r => true)).Round);
    }

    public static TheoryData<string, Func<Guid, string[]>> Inconsistent() => new()
    {
        { "device names repeat", boot => [
            Part(boot, 0, 0, 3, devices: [Device("Line", 2)]),
            Part(boot, 0, 1, 3, devices: [Device("LINE", 0)]),
            Part(boot, 0, 2, 3, tags: Tags("Line", "Line.Speed", "Line.Batch"))] },
        { "tags for an unknown device", boot => [
            Part(boot, 0, 0, 2, devices: [Device("Line", 0)]),
            Part(boot, 0, 1, 2, tags: Tags("Ghost", "Ghost.A"))] },
        { "tag count differs", boot => [
            Part(boot, 0, 0, 2, devices: [Device("Line", 3)]),
            Part(boot, 0, 1, 2, tags: Tags("Line", "Line.Speed", "Line.Batch"))] },
        { "tagId repeats", boot => [
            Part(boot, 0, 0, 3, devices: [Device("Line", 1), Device("Press", 1)]),
            Part(boot, 0, 1, 3, tags: Tags("Line", "Shared.Tag")),
            Part(boot, 0, 2, 3, tags: Tags("Press", "shared.tag"))] },
        { "commandsEnabled differs", boot => [
            Part(boot, 0, 0, 2, devices: [Device("Line", 1)]),
            Part(boot, 0, 1, 2, tags: Tags("Line", "Line.Speed"), commandsEnabled: false)] },
    };

    [Theory]
    [MemberData(nameof(Inconsistent))]
    public async Task InconsistentRound_IsDropped_WithOneFixedReason(string reason, Func<Guid, string[]> parts)
    {
        var logs = new CapturingLoggerProvider();
        await using var ctx = await StartAsync(database, logs: logs);

        foreach (var part in parts(Guid.NewGuid()))
        {
            await ctx.PublishAsync("edge-a", part);
        }

        await ctx.MarkerAsync();
        Assert.Null(await ctx.RowAsync("edge-a"));
        Assert.Equal(1, logs.Count(reason));
        Assert.Null(ctx.Ingestor.BufferOf("edge-a"));
    }

    [Fact]
    public async Task NewBootPerPart_KeepsOneBuffer_AndABufferStopsAt2400KiB()
    {
        await using var ctx = await StartAsync(database);

        // A broken Edge: every part is a new boot that never completes.
        for (var i = 0; i < 20; i++)
        {
            await ctx.PublishAsync("edge-a", Part(Guid.NewGuid(), 0, 0, 2, devices: [Device("Line", 0)]));
        }

        await ctx.MarkerAsync();
        Assert.Equal(1, ctx.Ingestor.BufferOf("edge-a")!.Value.Parts);

        // Parts of about 15 KiB for one round of 200: the buffer refuses what would pass 2400 KiB.
        var boot = Guid.NewGuid();
        for (var i = 1; i < 200; i++)
        {
            await ctx.PublishAsync("edge-a", Part(boot, 0, i, 200, tags: Tags("Line", i, size: 15 * 1024)));
        }

        await ctx.MarkerAsync();
        var buffer = ctx.Ingestor.BufferOf("edge-a")!.Value;
        Assert.True(buffer.Bytes <= InventoryIngestor.MaxBufferBytes, $"{buffer.Bytes} bytes buffered");
        Assert.InRange(buffer.Parts, 150, 165);
    }

    [Fact]
    public async Task TwoHundredPartsOf12KiB_Complete()
    {
        await using var ctx = await StartAsync(database);
        var boot = Guid.NewGuid();
        var tagParts = Enumerable.Range(1, 199).Select(i => Part(boot, 0, i, 200, tags: Tags("Line", i, size: 12 * 1024))).ToList();
        var tags = tagParts.Sum(p => ContractJson.Deserialize<EdgeInventory>(p).Tags!.Items.Count);
        Assert.All(tagParts, p => Assert.True(System.Text.Encoding.UTF8.GetByteCount(p) <= 12 * 1024));

        await ctx.PublishAsync("edge-a", Part(boot, 0, 0, 200, devices: [Device("Line", tags)]));
        foreach (var part in tagParts)
        {
            await ctx.PublishAsync("edge-a", part);
        }

        var row = await ctx.WaitForRowAsync("edge-a", r => true, TimeSpan.FromSeconds(60));
        Assert.Equal(tags, Document(row).Devices[0].Tags.Count);
    }

    [Fact]
    public async Task IncompleteBuffer_ExpiresAfterFiveMinutes()
    {
        var clock = new FakeTimeProvider(Sent);
        await using var ctx = await StartAsync(database, clock);
        var boot = Guid.NewGuid();
        var parts = Round(boot, 0);

        await ctx.PublishAsync("edge-a", parts[0]);
        await ctx.MarkerAsync();
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        // The buffer holding part 0 is gone: parts 1 and 2 start a fresh one, which part 0 then completes.
        await ctx.PublishAsync("edge-a", parts[1]);
        await ctx.PublishAsync("edge-a", parts[2]);
        await ctx.MarkerAsync();
        Assert.Null(await ctx.RowAsync("edge-a"));

        await ctx.PublishAsync("edge-a", parts[0]);
        await ctx.WaitForRowAsync("edge-a", r => true);
    }

    [Fact]
    public async Task Order_SameBootOlderRoundLoses_NewBootWins_AndASupersededBootIsDropped()
    {
        var clock = new FakeTimeProvider(Sent);
        var logs = new CapturingLoggerProvider();
        await using var ctx = await StartAsync(database, clock, logs);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await ctx.PublishRoundAsync("edge-a", Round(a, 5));
        await ctx.WaitForRowAsync("edge-a", r => r.BootId == a && r.Round == 5);

        clock.Advance(TimeSpan.FromSeconds(6));
        await ctx.PublishRoundAsync("edge-a", Round(a, 5)); // equal
        await ctx.SettledAsync("edge-a");
        clock.Advance(TimeSpan.FromSeconds(6));
        await ctx.PublishRoundAsync("edge-a", Round(a, 4)); // older
        await ctx.SettledAsync("edge-a");
        Assert.Equal((a, 5L), await ctx.BootAndRoundAsync("edge-a"));

        clock.Advance(TimeSpan.FromSeconds(6));
        await ctx.PublishRoundAsync("edge-a", Round(b, 0)); // a new boot (the agent restarted)
        await ctx.WaitForRowAsync("edge-a", r => r.BootId == b);

        // A second process still running with boot A: dropped, and said so.
        clock.Advance(TimeSpan.FromSeconds(6));
        await ctx.PublishRoundAsync("edge-a", Round(a, 6));
        await ctx.SettledAsync("edge-a");
        Assert.Equal((b, 0L), await ctx.BootAndRoundAsync("edge-a"));
        Assert.True(logs.Count("two Edge processes share edgeId") >= 1);

        // A boot that never had a stored document is not remembered: its round is accepted.
        var c = Guid.NewGuid();
        await ctx.PublishAsync("edge-a", Round(c, 0)[0]); // C opens a buffer...
        clock.Advance(TimeSpan.FromSeconds(6));
        await ctx.PublishRoundAsync("edge-a", Round(b, 1)); // ...that B replaces before C completes
        await ctx.WaitForRowAsync("edge-a", r => r.BootId == b && r.Round == 1);
        var warnings = logs.Count("two Edge processes share edgeId");
        clock.Advance(TimeSpan.FromSeconds(6));
        await ctx.PublishRoundAsync("edge-a", Round(c, 1));
        await ctx.WaitForRowAsync("edge-a", r => r.BootId == c);
        Assert.Equal(warnings, logs.Count("two Edge processes share edgeId"));
    }

    [Fact]
    public async Task CompleteRoundsOneSecondApart_AreStoredAtMostOncePerFiveSeconds_NewestWinning_AsSoonAsTheGapAllows()
    {
        var clock = new FakeTimeProvider(Sent);
        await using var ctx = await StartAsync(database, clock);
        var boot = Guid.NewGuid();

        await ctx.PublishRoundAsync("edge-a", Round(boot, 1));
        await ctx.WaitForRowAsync("edge-a", r => r.Round == 1);
        var firstStored = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromSeconds(1));
        await ctx.PublishRoundAsync("edge-a", Round(boot, 2));
        clock.Advance(TimeSpan.FromSeconds(1));
        await ctx.PublishRoundAsync("edge-a", Round(boot, 3));
        var thirdCompleted = clock.GetUtcNow();
        await ctx.PublishRoundAsync("edge-a", Round(boot, 2)); // late: never replaces the newer waiting round
        await ctx.MarkerAsync();
        Assert.Equal((boot, 1L), await ctx.BootAndRoundAsync("edge-a"));

        // No further message: the loop stores the waiting round once the 5 s gap has passed, and only then. The clock stops
        // at the gap plus one 1 s tick, so the store must happen by then (a real-time wait, the clock no longer moves).
        await StepClockAsync(clock, firstStored + TimeSpan.FromSeconds(4.5) - clock.GetUtcNow());
        Assert.Equal((boot, 1L), await ctx.BootAndRoundAsync("edge-a"));
        await StepClockAsync(clock, firstStored + TimeSpan.FromSeconds(6) - clock.GetUtcNow());
        var row = await ctx.WaitForRowAsync("edge-a", r => r.Round > 1);

        Assert.Equal(3, row.Round);
        Assert.Equal(thirdCompleted, row.ReceivedUtc); // when it completed, not when it was stored
    }

    [Fact]
    public async Task DatabaseDown_NeverHoldsTheMessagePipeline_NorForcesAReconnect_AndTheWaitingRoundIsStoredOnceItIsBack()
    {
        var clock = new FakeTimeProvider(Sent);
        await using var ctx = await StartAsync(database, clock);
        var attempts = ctx.Host.Broker.ConnectAttempts.Count;
        var boot = Guid.NewGuid();

        ctx.Host.DatabaseFaults.Enabled = true;
        var commands = ctx.Host.DatabaseFaults.CommandCount;
        await ctx.PublishRoundAsync("edge-a", Round(boot, 0));
        var completed = clock.GetUtcNow();
        await WaitUntilAsync(() => Task.FromResult(ctx.Host.DatabaseFaults.CommandCount > commands), "first store attempt");

        // While the store keeps failing, inventory messages are still handled at once (the handler never waits on the database).
        await ctx.PublishAsync("edge-a", Part(Guid.NewGuid(), 0, 0, 2, devices: [Device("Line", 0)]));
        await WaitUntilAsync(() => Task.FromResult(ctx.Ingestor.BufferOf("edge-a") is { Parts: 1 }), "a part handled during the outage");
        Assert.True(ctx.Ingestor.IsWaiting("edge-a"));

        // Past the 30 s retry budget: the round stays waiting, the connection stays up, and it is stored once the database is back.
        await StepClockAsync(clock, DatabaseRetry.Budget + TimeSpan.FromSeconds(5));
        Assert.True(ctx.Ingestor.IsWaiting("edge-a"));
        ctx.Host.DatabaseFaults.Enabled = false;
        await StepUntilAsync(clock, async () => await ctx.RowAsync("edge-a") is not null, "the waiting round stored after the outage");

        var row = (await ctx.RowAsync("edge-a"))!;
        Assert.Equal((boot, 0L), (row.BootId, row.Round));
        Assert.Equal(completed, row.ReceivedUtc);
        Assert.Equal(attempts, ctx.Host.Broker.ConnectAttempts.Count);
        Assert.True(ctx.Connection.IsConnected);
    }

    [Fact]
    public async Task UnexpectedStoreFailure_KeepsTheHostRunning_AndTheRoundIsStoredOnTheNextPass()
    {
        var logs = new CapturingLoggerProvider();
        var failure = new InventoryFailure();
        await using var ctx = await StartAsync(database, logs: logs, configureServices: services =>
            services.AddSingleton<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>(failure));

        // Not a database outage: DatabaseRetry does not handle it, so only the loop's own guard keeps the Cloud alive.
        failure.Enabled = true;
        await ctx.PublishRoundAsync("edge-a", Round(Guid.NewGuid(), 0));
        await WaitUntilAsync(() => Task.FromResult(logs.Count("Inventory store pass failed") >= 1), "a failed store pass");
        Assert.True(ctx.Ingestor.IsWaiting("edge-a"));

        failure.Enabled = false;
        await ctx.WaitForRowAsync("edge-a", r => true);
        await ctx.MarkerAsync(); // telemetry still flows: the host did not stop
    }

    [Fact]
    public async Task ForeignMismatchedAndInvalidParts_AreDropped_NamingTopicAndReasonOnly()
    {
        var logs = new CapturingLoggerProvider();
        await using var ctx = await StartAsync(database, logs: logs);
        var boot = Guid.NewGuid();

        await ctx.Host.Broker.PublishAsync("iiot/edge-z/inventory", Round(boot, 0)[0].Replace("edge-a", "edge-z", StringComparison.Ordinal));
        await ctx.PublishAsync("edge-b", Round(boot, 0)[0]); // says edge-a on edge-b's topic
        await ctx.PublishAsync("edge-a", "{\"schemaVersion\":1,\"edgeId\":\"edge-a\",\"secret-looking\":\"payload\"}");
        await ctx.MarkerAsync();

        Assert.Null(await ctx.RowAsync("edge-a"));
        Assert.Null(await ctx.RowAsync("edge-b"));
        Assert.Equal(1, logs.Count("unknown edge"));
        Assert.Equal(1, logs.Count("payload edgeId does not match the topic"));
        Assert.Equal(1, logs.Count("invalid payload"));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("secret-looking", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migration_AppliesOnTheCurrentSchema_KeepingTelemetry()
    {
        var options = new DbContextOptionsBuilder<CloudDbContext>().UseNpgsql(database.NewDatabaseConnectionString()).Options;
        await using var db = new CloudDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261019004604_TelemetryEdgeReceivedIndex");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO telemetry (edge_id, stream_id, sequence, tag_id, value, quality, timestamp_utc, received_utc) "
            + "VALUES ('edge-a', gen_random_uuid(), 1, 't1', '1', 'good', now(), now())");

        await migrator.MigrateAsync();

        Assert.Equal(1, await db.Telemetry.CountAsync());
        Assert.Equal(0, await db.EdgeInventory.CountAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static InventoryDocument Document(EdgeInventoryRow row) =>
        JsonSerializer.Deserialize<InventoryDocument>(row.Document, ContractJson.Options)!;

    private static string[] Round(Guid boot, long round) =>
    [
        Part(boot, round, 0, 3, devices: [Device("Line", 2), Device("Press", 1) with { Model = "FX5U" }]),
        Part(boot, round, 1, 3, tags: new InventoryTags
        {
            Device = "Line",
            Items = [Tag("Line.Speed") with { Name = "Speed", Description = "Belt speed" }, Tag("Line.Batch") with { Name = "Batch" }],
        }),
        Part(boot, round, 2, 3, tags: Tags("Press", "Press.Count")),
    ];

    private static string Part(
        Guid boot, long round, int part, int partCount, IReadOnlyList<InventoryDevice>? devices = null, InventoryTags? tags = null, bool commandsEnabled = true) =>
        ContractJson.Serialize(new EdgeInventory
        {
            SchemaVersion = ContractJson.SchemaVersion,
            EdgeId = "edge-a",
            BootId = boot,
            Round = round,
            Part = part,
            PartCount = partCount,
            SentUtc = Sent,
            CommandsEnabled = commandsEnabled,
            Devices = devices,
            Tags = tags,
        });

    private static InventoryDevice Device(string name, int tagCount) => new()
    {
        Name = name,
        Model = "S7-1200",
        Enabled = true,
        WritesEnabled = false,
        State = InventoryDeviceState.Connected,
        LastGoodUtc = Sent,
        LastError = null,
        TagCount = tagCount,
    };

    private static InventoryTags Tags(string device, params string[] tagIds) => new()
    {
        Device = device,
        Items = [.. tagIds.Select(Tag)],
    };

    /// <summary>
    /// Tags whose part (as <see cref="Part"/> serializes it) stays within <paramref name="size"/> bytes and comes close
    /// to it, ids unique per <paramref name="partIndex"/>. Measured, not estimated.
    /// </summary>
    private static InventoryTags Tags(string device, int partIndex, int size)
    {
        var items = new List<InventoryTag>();
        for (var t = 0; ; t++)
        {
            var next = Tag($"{device}.P{partIndex:000}T{t:00}") with { Description = new string('x', 256) };
            var candidate = new InventoryTags { Device = device, Items = [.. items, next] };
            if (System.Text.Encoding.UTF8.GetByteCount(Part(Guid.NewGuid(), 0, 199, 200, tags: candidate)) > size)
            {
                return new InventoryTags { Device = device, Items = items };
            }

            items.Add(next);
        }
    }

    private static InventoryTag Tag(string tagId) => new()
    {
        TagId = tagId,
        Name = tagId,
        DataType = "Int",
        Length = null,
        Writable = false,
        Min = null,
        Max = null,
        Description = null,
    };

    private static async Task<Context> StartAsync(
        PostgresFixture database,
        FakeTimeProvider? clock = null,
        CapturingLoggerProvider? logs = null,
        Action<CloudTestHost>? beforeBuild = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = new CloudTestHost(database, configureServices: services =>
        {
            if (clock is not null)
            {
                services.AddSingleton<TimeProvider>(clock);
            }

            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }

            configureServices?.Invoke(services);
        });
        beforeBuild?.Invoke(host);
        var client = host.CreateClient();
        var connection = host.Services.GetRequiredService<CloudMqttConnection>();
        await connection.FirstAttemptCompleted.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(connection.IsConnected);
        var ingestor = host.Services.GetServices<IHostedService>().OfType<InventoryIngestor>().Single();
        return new Context(host, client, connection, ingestor);
    }

    private static async Task StepClockAsync(FakeTimeProvider clock, TimeSpan total)
    {
        var step = TimeSpan.FromMilliseconds(250);
        for (var moved = TimeSpan.Zero; moved < total; moved += step)
        {
            clock.Advance(step);
            await Task.Delay(20);
        }
    }

    private static async Task StepUntilAsync(FakeTimeProvider clock, Func<Task<bool>> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), $"timed out waiting for: {what}");
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(30);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < (timeout ?? TimeSpan.FromSeconds(20)), $"timed out waiting for: {what}");
            await Task.Delay(50);
        }
    }

    private sealed record Context(CloudTestHost Host, HttpClient Client, CloudMqttConnection Connection, InventoryIngestor Ingestor)
        : IAsyncDisposable
    {
        private int _markers;

        public Task PublishAsync(string topicEdgeId, string payload) => Host.Broker.PublishAsync($"iiot/{topicEdgeId}/inventory", payload);

        public async Task PublishRoundAsync(string topicEdgeId, string[] parts)
        {
            foreach (var part in parts)
            {
                await PublishAsync(topicEdgeId, part);
            }
        }

        public Task PublishTelemetryAsync(string edgeId, long sequence) => Host.Broker.PublishAsync(
            $"iiot/{edgeId}/telemetry",
            ContractJson.Serialize(new TelemetryPayload
            {
                SchemaVersion = ContractJson.SchemaVersion,
                EdgeId = edgeId,
                StreamId = MarkerStream,
                Sequence = sequence,
                TagId = "marker",
                Value = JsonSerializer.SerializeToElement(1),
                Quality = TelemetryQuality.Good,
                TimestampUtc = DateTimeOffset.UnixEpoch,
            }));

        /// <summary>Messages are handled in order: once this telemetry sample is stored, everything before it was handled.</summary>
        public async Task MarkerAsync()
        {
            var count = await CountTelemetryAsync();
            await PublishTelemetryAsync("edge-b", 1_000 + Interlocked.Increment(ref _markers));
            await WaitUntilAsync(async () => await CountTelemetryAsync() > count, "marker stored");
        }

        /// <summary>Every message before this point was handled, and the loop has stored or discarded the Edge's waiting round.</summary>
        public async Task SettledAsync(string edgeId)
        {
            await MarkerAsync();
            await WaitUntilAsync(() => Task.FromResult(!Ingestor.IsWaiting(edgeId)), $"no waiting round for {edgeId}");
        }

        public Task<int> CountTelemetryAsync() => QueryAsync(db => db.Telemetry.CountAsync());

        public Task<EdgeInventoryRow?> RowAsync(string edgeId) =>
            QueryAsync(db => db.EdgeInventory.AsNoTracking().SingleOrDefaultAsync(r => r.EdgeId == edgeId));

        public async Task<(Guid, long)> BootAndRoundAsync(string edgeId)
        {
            var row = await RowAsync(edgeId);
            Assert.NotNull(row);
            return (row.BootId, row.Round);
        }

        public async Task<EdgeInventoryRow> WaitForRowAsync(string edgeId, Func<EdgeInventoryRow, bool> match, TimeSpan? timeout = null)
        {
            EdgeInventoryRow? row = null;
            await WaitUntilAsync(async () => (row = await RowAsync(edgeId)) is { } r && match(r), $"inventory row of {edgeId}", timeout);
            return row!;
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

        private static readonly Guid MarkerStream = Guid.NewGuid();
    }

    /// <summary>While enabled, fails commands on edge_inventory with an error that is not a database outage.</summary>
    private sealed class InventoryFailure : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private volatile bool _enabled;

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            _enabled && command.CommandText.Contains("edge_inventory", StringComparison.Ordinal)
                ? throw new InvalidOperationException("Simulated bug in the inventory store.")
                : ValueTask.FromResult(result);
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
                messages.Enqueue(formatter(state, exception));
        }
    }
}
