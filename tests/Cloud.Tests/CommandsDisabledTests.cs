using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IndustrialIot.Cloud.Commands;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Cloud.Tests.Infrastructure;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Npgsql;

namespace IndustrialIot.Cloud.Tests;

/// <summary>
/// Read-only by default: unless <c>Cloud:Commands:Enabled</c> is true the Cloud has no command route, consumes no command
/// result and runs no timeout sweep. The test host turns commands on for the other tests; here the key is removed.
/// </summary>
[Collection(CloudCollection.Name)]
public class CommandsDisabledTests(PostgresFixture database)
{
    private const string Body = """{"tagId":"Line1.Speed","value":1200,"ttlSeconds":45}""";

    [Theory]
    [InlineData("Operator")]
    [InlineData("Viewer")]
    public async Task Default_EveryCommandRoute_Is404(string role)
    {
        await using var ctx = await StartAsync(database, enabled: null);
        var token = ctx.Host.CreateToken([role], sub: "user-1");

        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/v1/edges/edge-a/commands")
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.SendAsync(post, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/v1/commands/{Guid.NewGuid()}"), token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/edges/edge-a/commands"), token)).StatusCode);
    }

    [Fact]
    public async Task Default_NoResultSubscription_RowsUntouched_NoSweeper()
    {
        await using var ctx = await StartAsync(database, enabled: null);

        // A row an earlier write-mode run left waiting for its result.
        var commandId = Guid.NewGuid();
        await ctx.QueryAsync(async db =>
        {
            db.Commands.Add(new CommandRow
            {
                CommandId = commandId,
                EdgeId = "edge-a",
                TagId = "Line1.Speed",
                TargetValue = "1200",
                IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(4),
                IssuedBy = "operator-1",
                Status = CommandStatus.Published,
                CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            return await db.SaveChangesAsync();
        });

        await ctx.Host.Broker.PublishAsync("iiot/edge-a/command-results", ContractJson.Serialize(new CommandResult
        {
            SchemaVersion = ContractJson.SchemaVersion,
            CommandId = commandId,
            EdgeId = "edge-a",
            Outcome = CommandOutcome.Applied,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        }));

        // Messages are handled in order: once this later telemetry sample is stored, the result had its chance.
        await ctx.Host.Broker.PublishAsync("iiot/edge-a/telemetry", Telemetry());
        await WaitUntilAsync(async () => await ctx.QueryAsync(db => db.Telemetry.CountAsync()) == 1, "telemetry marker stored");

        Assert.Equal(CommandStatus.Published, await ctx.QueryAsync(db => db.Commands.Where(c => c.CommandId == commandId).Select(c => c.Status).SingleAsync()));
        Assert.DoesNotContain(ctx.Subscriptions, s => s == ("iiot-cloud", CommandResultIngestor.TopicFilter));
        Assert.Contains(ctx.Subscriptions, s => s == ("iiot-cloud", "iiot/+/telemetry")); // the recorder saw the Cloud subscribe
        var sweeper = ctx.Host.Services.GetServices<IHostedService>().OfType<CommandTimeoutSweeper>().Single();
        Assert.Equal(0, sweeper.WaitsArmed);
    }

    [Fact]
    public async Task RestartFromWriteMode_QueuedResultsDiscarded_NotRedelivered()
    {
        var logs = new CommandTestKit.LogCapture();
        var unsubscriptions = new ConcurrentQueue<(string ClientId, string Topic)>();
        await using var ctx = await StartAsync(database, enabled: null, logs, async host =>
        {
            host.Broker.Server.InterceptingUnsubscriptionAsync += e =>
            {
                unsubscriptions.Enqueue((e.ClientId, e.Topic));
                return Task.CompletedTask;
            };

            // A write-mode run under the Cloud's client id leaves the result subscription in the persistent session.
            using (var writeMode = new MqttClientFactory().CreateMqttClient())
            {
                await writeMode.ConnectAsync(new MqttClientOptionsBuilder()
                    .WithTcpServer("localhost", host.Broker.Port)
                    .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
                    .WithClientId("iiot-cloud")
                    .WithCredentials(host.Broker.Username, host.Broker.Password)
                    .WithCleanStart(false)
                    .WithSessionExpiryInterval(3600)
                    // Test-only: the broker's certificate comes from a CA generated for this run.
                    .WithTlsOptions(tls => tls.UseTls(true).WithCertificateValidationHandler(_ => true))
                    .Build());
                await writeMode.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(CommandResultIngestor.TopicFilter).WithAtLeastOnceQoS())
                    .Build());
                await writeMode.DisconnectAsync();
            }

            // Queued for the offline session; the host that picks it up has commands switched off.
            for (var i = 0; i < 30; i++)
            {
                await host.Broker.PublishAsync("iiot/edge-a/command-results", ContractJson.Serialize(new CommandResult
                {
                    SchemaVersion = ContractJson.SchemaVersion,
                    CommandId = Guid.NewGuid(),
                    EdgeId = "edge-a",
                    Outcome = CommandOutcome.Applied,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                }));
            }
        });

        await WaitUntilAsync(() => Task.FromResult(Count(logs, "Discarded a message on retired topic") >= 30), "30 queued results discarded");
        await WaitUntilAsync(() => Task.FromResult(unsubscriptions.Contains(("iiot-cloud", CommandResultIngestor.TopicFilter))), "the result filter unsubscribed");

        // Telemetry is still stored and acknowledged.
        await ctx.Host.Broker.PublishAsync("iiot/edge-a/telemetry", Telemetry(sequence: 1));
        await WaitUntilAsync(async () => await ctx.QueryAsync(db => db.Telemetry.CountAsync()) == 1, "telemetry stored");
        await WaitUntilAsync(() => Task.FromResult(AckCount(ctx) == 1), "telemetry acked");

        // Same broker, same session: nothing on the retired filter comes back.
        var connection = ctx.Host.Services.GetRequiredService<CloudMqttConnection>();
        var attempts = ctx.Host.Broker.ConnectAttempts.Count;
        await connection.ForceReconnectAsync();
        await WaitUntilAsync(() => Task.FromResult(ctx.Host.Broker.ConnectAttempts.Count > attempts && connection.IsConnected), "reconnected");
        await ctx.Host.Broker.PublishAsync("iiot/edge-a/telemetry", Telemetry(sequence: 2));
        await WaitUntilAsync(() => Task.FromResult(AckCount(ctx) == 2), "second telemetry acked");

        Assert.Equal(30, Count(logs, "Discarded a message on retired topic"));
        Assert.Equal(0, Count(logs, "Ignoring a message"));
        Assert.Equal(2, await ctx.QueryAsync(db => db.Telemetry.CountAsync()));
    }

    [Fact]
    public async Task NonBooleanValue_FailsStartNamingTheKey()
    {
        await using var host = new CloudTestHost(database, new Dictionary<string, string?> { ["Cloud:Commands:Enabled"] = "yes" });

        var error = Record.Exception(() => host.CreateClient());

        Assert.NotNull(error);
        Assert.Contains("Cloud:Commands:Enabled", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_PostIsAccepted()
    {
        await using var ctx = await StartAsync(database, enabled: "true");

        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/v1/edges/edge-a/commands")
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        var response = await ctx.SendAsync(post, ctx.Host.CreateToken(["Operator"], sub: "operator-1"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(ctx.Subscriptions, s => s == ("iiot-cloud", CommandResultIngestor.TopicFilter));
    }

    private static readonly Guid Stream = Guid.NewGuid();

    private static int Count(CommandTestKit.LogCapture logs, string fragment) =>
        logs.Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

    private static int AckCount(Context ctx) => ctx.Host.Broker.Received.Count(m => m.Topic == "iiot/edge-a/telemetry-acks");

    private static string Telemetry(long sequence = 1) => ContractJson.Serialize(new TelemetryPayload
    {
        SchemaVersion = ContractJson.SchemaVersion,
        EdgeId = "edge-a",
        StreamId = Stream,
        Sequence = sequence,
        TagId = "marker",
        Value = JsonSerializer.SerializeToElement(1.0),
        Quality = TelemetryQuality.Good,
        TimestampUtc = DateTimeOffset.UtcNow,
    });

    /// <param name="enabled">The <c>Cloud:Commands:Enabled</c> value; null removes the key (the test host sets it to true).</param>
    /// <param name="beforeBuild">Runs against the broker before the host is built and connects.</param>
    private static async Task<Context> StartAsync(
        PostgresFixture database, string? enabled, ILoggerProvider? logs = null, Func<CloudTestHost, Task>? beforeBuild = null)
    {
        var host = new CloudTestHost(
            database,
            new Dictionary<string, string?> { ["Cloud:Commands:Enabled"] = enabled },
            logs is null ? null : services => services.AddSingleton(logs));
        if (beforeBuild is not null)
        {
            await beforeBuild(host);
        }

        var subscriptions = new ConcurrentQueue<(string ClientId, string Topic)>();
        host.Broker.Server.InterceptingSubscriptionAsync += e =>
        {
            subscriptions.Enqueue((e.ClientId, e.TopicFilter.Topic));
            return Task.CompletedTask;
        };

        var client = host.CreateClient();
        var connection = host.Services.GetRequiredService<CloudMqttConnection>();
        await connection.FirstAttemptCompleted.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(connection.IsConnected);
        return new Context(host, client, subscriptions);
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

    private sealed record Context(CloudTestHost Host, HttpClient Client, ConcurrentQueue<(string ClientId, string Topic)> Subscriptions)
        : IAsyncDisposable
    {
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string token)
        {
            using (request)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await Client.SendAsync(request);
            }
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

            // One database per host, so one pool per host: release its idle connections for the tests that follow.
            NpgsqlConnection.ClearAllPools();
        }
    }
}
