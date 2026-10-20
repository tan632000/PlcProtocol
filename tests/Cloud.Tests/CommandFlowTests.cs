using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using IndustrialIot.Cloud.Commands;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Tests.Infrastructure;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MQTTnet.Server;

namespace IndustrialIot.Cloud.Tests;

[Collection(CloudCollection.Name)]
public class CommandFlowTests(PostgresFixture database)
{
    private const string Body = """{"tagId":"Line1.Speed","value":1200,"ttlSeconds":45}""";

    [Fact]
    public async Task Result_UpdatesStatus_FirstWins()
    {
        var logs = new CommandTestKit.LogCapture();
        await using var ctx = await CommandTestKit.StartAsync(database, configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        await using var edge = await CommandTestKit.FakeEdge.StartAsync(ctx.Broker, "edge-a", _ => CommandOutcome.Applied);
        var token = ctx.Host.CreateToken(["Operator"]);

        var first = await IdOfAsync(await ctx.PostAsync("edge-a", Body, token));
        await CommandTestKit.WaitUntilAsync(async () => (await ctx.RowAsync(first)).Status == "applied", "Edge outcome recorded");
        var recorded = await ctx.RowAsync(first);
        Assert.NotNull(recorded.CompletedAtUtc);
        Assert.NotNull(recorded.ResultReceivedUtc);

        // A second, different answer for the same command, then a fresh command as a marker:
        // results are handled in order, so once the marker is recorded the second answer has been handled.
        await ctx.InjectResultAsync("edge-a", "edge-a", first, CommandOutcome.Failed, CommandReasons.PlcUnavailable);
        var marker = await IdOfAsync(await ctx.PostAsync("edge-a", Body, token));
        await CommandTestKit.WaitUntilAsync(async () => (await ctx.RowAsync(marker)).Status == "applied", "marker recorded");

        var after = await ctx.RowAsync(first);
        Assert.Equal("applied", after.Status);
        Assert.Null(after.Reason);
        Assert.Equal(recorded.CompletedAtUtc, after.CompletedAtUtc);
        Assert.Contains(logs.Messages, m => m.Contains($"Command {first} already ended as applied", StringComparison.Ordinal));
        Assert.Equal(2, edge.Received.Count);
    }

    [Fact]
    public async Task EarlyResult_NotOverwritten()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);

        // The Edge answers before the broker has acknowledged the command to the Cloud: the PUBACK is held
        // until the Cloud has stored the result, so the result lands while the row is still `pending`.
        var resultStored = false;
        ctx.Broker.Server.InterceptingPublishAsync += async e =>
        {
            if (e.ClientId != "iiot-cloud" || !e.ApplicationMessage.Topic.EndsWith("/commands", StringComparison.Ordinal))
            {
                return;
            }

            var command = ContractJson.Deserialize<DeviceCommand>(Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray()));
            Assert.Equal(CommandStatus.Pending, (await ctx.RowAsync(command.CommandId)).Status);
            await ctx.InjectResultAsync("edge-a", "edge-a", command.CommandId, CommandOutcome.Applied);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(8))
            {
                if ((await ctx.RowAsync(command.CommandId)).Status == "applied")
                {
                    resultStored = true;
                    return;
                }

                await Task.Delay(50);
            }
        };

        var response = await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Operator"]));

        Assert.True(resultStored, "the early result was not stored while the command was pending");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await CommandTestKit.JsonAsync(response);
        Assert.Equal("applied", body.GetProperty("status").GetString()); // the response shows the real outcome
        Assert.Equal("applied", (await ctx.RowAsync(body.GetProperty("commandId").GetGuid())).Status);
    }

    [Fact]
    public async Task NoResult_TimesOut_LateResultWins()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var ctx = await CommandTestKit.StartAsync(database, clock, new Dictionary<string, string?>
        {
            ["Cloud:Commands:TimeoutGraceSeconds"] = "20",
        });
        var sweeper = Sweeper(ctx);
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(sweeper.WaitsArmed >= 1), "sweeper waiting");

        // ttl 5 s + grace 20 s: due at +25 s.
        var id = await IdOfAsync(await ctx.PostAsync("edge-a", """{"tagId":"Line1.Speed","value":1,"ttlSeconds":5}""", ctx.Host.CreateToken(["Operator"])));

        await SweepAfterAsync(clock, sweeper, TimeSpan.FromSeconds(24));
        Assert.Equal(CommandStatus.Published, (await ctx.RowAsync(id)).Status);

        await SweepAfterAsync(clock, sweeper, TimeSpan.FromSeconds(10));
        Assert.Equal(CommandStatus.TimedOut, (await ctx.RowAsync(id)).Status);

        // The Edge did answer after all: its outcome is the truth about the PLC.
        await ctx.InjectResultAsync("edge-a", "edge-a", id, CommandOutcome.Applied);
        await CommandTestKit.WaitUntilAsync(async () => (await ctx.RowAsync(id)).Status == "applied", "late result recorded");
    }

    [Fact]
    public async Task PendingOrUnknown_TimesOut()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var ctx = await CommandTestKit.StartAsync(database, clock);
        var sweeper = Sweeper(ctx);
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(sweeper.WaitsArmed >= 1), "sweeper waiting");

        var past = clock.GetUtcNow().AddHours(-1);
        var rows = new Dictionary<string, Guid>();
        await using (var scope = ctx.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();

            // A crash between storing and publishing leaves `pending`; a lost PUBACK leaves `publish_unknown`.
            foreach (var (name, status, expires) in new[]
                     {
                         ("pending", CommandStatus.Pending, past),
                         ("unknown", CommandStatus.PublishUnknown, past),
                         ("published", CommandStatus.Published, past),
                         ("applied", "applied", past),
                         ("not_sent", CommandStatus.NotSent, past),
                         ("fresh", CommandStatus.Pending, clock.GetUtcNow().AddMinutes(10)),
                     })
            {
                var id = Guid.NewGuid();
                rows[name] = id;
                db.Commands.Add(new CommandRow
                {
                    CommandId = id,
                    EdgeId = "edge-a",
                    TagId = "t1",
                    TargetValue = "1",
                    IssuedAtUtc = expires.AddSeconds(-60),
                    ExpiresAtUtc = expires,
                    IssuedBy = "operator-1",
                    Status = status,
                    CreatedUtc = expires.AddSeconds(-60),
                });
            }

            await db.SaveChangesAsync();
        }

        await SweepAfterAsync(clock, sweeper, TimeSpan.FromSeconds(10));

        Assert.Equal(CommandStatus.TimedOut, (await ctx.RowAsync(rows["pending"])).Status);
        Assert.Equal(CommandStatus.TimedOut, (await ctx.RowAsync(rows["unknown"])).Status);
        Assert.Equal(CommandStatus.TimedOut, (await ctx.RowAsync(rows["published"])).Status);
        Assert.Equal("applied", (await ctx.RowAsync(rows["applied"])).Status);
        Assert.Equal(CommandStatus.NotSent, (await ctx.RowAsync(rows["not_sent"])).Status);
        Assert.Equal(CommandStatus.Pending, (await ctx.RowAsync(rows["fresh"])).Status);
    }

    [Fact]
    public async Task SpoofedResult_Dropped()
    {
        var logs = new CommandTestKit.LogCapture();
        await using var ctx = await CommandTestKit.StartAsync(database, configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        var id = await IdOfAsync(await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Operator"])));

        await ctx.InjectResultAsync("edge-b", "edge-a", id, CommandOutcome.Failed);  // payload claims another Edge
        await ctx.InjectResultAsync("edge-b", "edge-b", id, CommandOutcome.Failed);  // another Edge answering edge-a's command
        await ctx.InjectResultAsync("edge-x", "edge-x", id, CommandOutcome.Failed);  // Edge not allow-listed
        await ctx.InjectResultAsync("edge-a", "edge-a", Guid.NewGuid(), CommandOutcome.Failed); // no such command
        await ctx.Broker.PublishAsync("iiot/edge-a/command-results", "{\"commandId\":");  // malformed

        // The genuine answer, handled after all of the above.
        await ctx.InjectResultAsync("edge-a", "edge-a", id, CommandOutcome.Applied);
        await CommandTestKit.WaitUntilAsync(async () => (await ctx.RowAsync(id)).Status != CommandStatus.Published, "a result recorded");

        Assert.Equal("applied", (await ctx.RowAsync(id)).Status);
        Assert.Equal(5, logs.Messages.Count(m => m.StartsWith("Dropping a command result", StringComparison.Ordinal)));
    }

    private static CommandTimeoutSweeper Sweeper(CommandTestKit.Context ctx) =>
        ctx.Host.Services.GetServices<IHostedService>().OfType<CommandTimeoutSweeper>().Single();

    /// <summary>Moves the clock and waits for exactly the one sweep that the move triggers.</summary>
    private static async Task SweepAfterAsync(FakeTimeProvider clock, CommandTimeoutSweeper sweeper, TimeSpan by)
    {
        var armed = sweeper.WaitsArmed;
        clock.Advance(by);
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(sweeper.WaitsArmed > armed), "sweep done");
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await CommandTestKit.JsonAsync(response)).GetProperty("commandId").GetGuid();
    }
}
