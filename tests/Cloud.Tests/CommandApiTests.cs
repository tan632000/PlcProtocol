using System.Buffers;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IndustrialIot.Cloud.Commands;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Cloud.Tests.Infrastructure;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace IndustrialIot.Cloud.Tests;

[Collection(CloudCollection.Name)]
public class CommandApiTests(PostgresFixture database)
{
    private const string Body = """{"tagId":"Line1.Speed","value":1200,"ttlSeconds":45}""";

    [Fact]
    public async Task Post_PublishesOnce_Audited()
    {
        var logs = new CommandTestKit.LogCapture();
        await using var ctx = await CommandTestKit.StartAsync(database, configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        var token = ctx.Host.CreateToken(["Operator"], sub: "operator-1");

        var response = await ctx.PostAsync("edge-a", Body, token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await CommandTestKit.JsonAsync(response);
        var commandId = body.GetProperty("commandId").GetGuid();
        Assert.Equal(CommandStatus.Published, body.GetProperty("status").GetString());
        Assert.Equal($"/api/v1/commands/{commandId}", response.Headers.Location?.OriginalString);

        // Exactly one DeviceCommand on the Edge's topic, carrying the caller's identity and the requested TTL.
        var published = Assert.Single(ctx.PublishedCommands());
        Assert.Equal("iiot/edge-a/commands", published.Topic);
        Assert.Equal("1200", published.Command.TargetValue!.Value.GetRawText());
        Assert.Equal(commandId, published.Command.CommandId);
        Assert.Equal("operator-1", published.Command.IssuedBy);
        Assert.Equal("Line1.Speed", published.Command.TagId);
        Assert.Equal(1200, published.Command.TargetValue!.Value.GetDouble());
        Assert.Equal(TimeSpan.FromSeconds(45), published.Command.ExpiresAtUtc - published.Command.IssuedAtUtc);
        Assert.Equal(published.Command.ExpiresAtUtc.UtcDateTime, body.GetProperty("expiresAtUtc").GetDateTime());

        var row = await ctx.RowAsync(commandId);
        Assert.Equal("operator-1", row.IssuedBy);
        Assert.Equal(CommandStatus.Published, row.Status);

        // Readable by a Viewer, alone and in the Edge's list.
        var viewer = ctx.Host.CreateToken(["Viewer"]);
        var detail = await CommandTestKit.JsonAsync(await ctx.GetAsync($"/api/v1/commands/{commandId}", viewer));
        Assert.Equal("operator-1", detail.GetProperty("issuedBy").GetString());
        Assert.Equal(1200, detail.GetProperty("value").GetDouble());
        var list = await CommandTestKit.JsonAsync(await ctx.GetAsync("/api/v1/edges/edge-a/commands?limit=10", viewer));
        Assert.Equal(commandId, Assert.Single(list.EnumerateArray().ToList()).GetProperty("commandId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.GetAsync($"/api/v1/commands/{Guid.NewGuid()}", viewer)).StatusCode);

        // Audit: one entry naming the command and its issuer, without the token or the value.
        var audit = Assert.Single(logs.Messages, m => m.Contains(commandId.ToString(), StringComparison.Ordinal) && m.Contains("issued by", StringComparison.Ordinal));
        Assert.Contains("operator-1", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("1200", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FloatToken_PublishedAsWritten()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);

        // The Edge rejects 1500.0 and 1e2 for integer tags (a sign of a mapping bug): the Cloud must not "fix" them.
        foreach (var token in new[] { "1500.0", "1e2" })
        {
            var response = await ctx.PostAsync("edge-a", $$"""{"tagId":"Line1.Speed","value":{{token}}}""", ctx.Host.CreateToken(["Operator"]));
            var id = (await CommandTestKit.JsonAsync(response)).GetProperty("commandId").GetGuid();
            Assert.Equal(token, ctx.PublishedCommands().Single(p => p.Command.CommandId == id).Command.TargetValue!.Value.GetRawText());
        }
    }

    [Fact]
    public async Task DatabaseFailureAfterPublish_StillAnswers()
    {
        var logs = new CommandTestKit.LogCapture();
        await using var ctx = await CommandTestKit.StartAsync(database, configureServices: s => s.AddSingleton<ILoggerProvider>(logs));

        // The database goes away between the PUBLISH and its PUBACK.
        ctx.Broker.Server.InterceptingPublishAsync += e =>
        {
            if (e.ClientId == "iiot-cloud" && e.ApplicationMessage.Topic.EndsWith("/commands", StringComparison.Ordinal))
            {
                ctx.Host.DatabaseFaults.Enabled = true;
            }

            return Task.CompletedTask;
        };

        var response = await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Operator"], sub: "operator-1"));
        ctx.Host.DatabaseFaults.Enabled = false;

        // Not a 500: the command went out, so the caller gets its id instead of a reason to retry blindly.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await CommandTestKit.JsonAsync(response)).GetProperty("commandId").GetGuid();
        Assert.Single(ctx.PublishedCommands());
        Assert.Contains(logs.Messages, m => m.Contains(id.ToString(), StringComparison.Ordinal) && m.Contains("issued by operator-1", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, m => m.Contains($"Command {id}: publish outcome", StringComparison.Ordinal));
        Assert.Equal(CommandStatus.Pending, (await ctx.RowAsync(id)).Status); // left for the result ingestor or the sweeper
    }

    [Fact]
    public async Task Post_RequiresOperator()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);

        Assert.Equal(HttpStatusCode.Unauthorized, (await ctx.PostAsync("edge-a", Body, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Viewer"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Operator"], sub: null))).StatusCode);

        Assert.Equal(0, await ctx.CountRowsAsync());
        Assert.Empty(ctx.PublishedCommands());
    }

    [Theory]
    [InlineData("""{"tagId":"Line1.Speed","value":1200,"ttlSeconds":4}""", null, "ttlSeconds")]
    [InlineData("""{"tagId":"Line1.Speed","value":1200,"ttlSeconds":601}""", null, "ttlSeconds")]
    [InlineData("""{"tagId":"Line1.Speed","value":"abc"}""", null, "targetValue")]
    [InlineData("""{"tagId":"Line1.Speed","value":null}""", null, "targetValue")]
    [InlineData("""{"tagId":"Line1.Speed","value":{"x":1}}""", null, "targetValue")]
    [InlineData("""{"value":1200}""", null, "tagId")]
    [InlineData("""{"tagId":"bad tag","value":1200}""", null, "tagId")]
    [InlineData("""{"tagId":"Line1.Speed","value":1200,"ttlSeconds":1.5}""", null, "body")]
    [InlineData("""{"tagId":"Line1.Speed","value":1,"value":2}""", null, "body")]
    [InlineData("""[1200]""", null, "body")]
    [InlineData("""{"tagId":""", null, "body")]
    [InlineData(Body, "k", "Idempotency-Key")] // key replaced by 129 characters below
    public async Task InvalidInput_NothingStored(string body, string? key, string field)
    {
        await using var ctx = await CommandTestKit.StartAsync(database);
        var token = ctx.Host.CreateToken(["Operator"]);

        var response = await ctx.PostAsync("edge-a", body, token, key is null ? null : new string('k', 129));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await CommandTestKit.JsonAsync(response);
        Assert.Equal("invalid_request", problem.GetProperty("title").GetString());
        Assert.StartsWith(field + ":", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var unknownEdge = await ctx.PostAsync("edge-x", Body, token);
        Assert.Equal(HttpStatusCode.NotFound, unknownEdge.StatusCode);
        Assert.Equal("edge_not_found", (await CommandTestKit.JsonAsync(unknownEdge)).GetProperty("title").GetString());

        Assert.Equal(0, await ctx.CountRowsAsync());
        Assert.Empty(ctx.PublishedCommands());
    }

    [Fact]
    public async Task IdempotencyKey_SequentialAndConcurrent()
    {
        var barrier = new CommandTestKit.InsertBarrier();
        await using var ctx = await CommandTestKit.StartAsync(database, configureServices: s => s.AddSingleton<IInterceptor>(barrier));
        var operator1 = ctx.Host.CreateToken(["Operator"], sub: "operator-1");

        // Sequential replay: same command, not published again.
        var first = await ctx.PostAsync("edge-a", Body, operator1, "k1");
        var second = await ctx.PostAsync("edge-a", Body, operator1, "k1");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var id = (await CommandTestKit.JsonAsync(first)).GetProperty("commandId").GetGuid();
        Assert.Equal(id, (await CommandTestKit.JsonAsync(second)).GetProperty("commandId").GetGuid());
        Assert.Equal($"/api/v1/commands/{id}", second.Headers.Location?.OriginalString);

        // 1200.0 is a different command for the Edge (rejected for integer tags), so it is not a replay of 1200.
        var asFloat = await ctx.PostAsync("edge-a", """{"tagId":"Line1.Speed","value":1200.0,"ttlSeconds":45}""", operator1, "k1");
        Assert.Equal(HttpStatusCode.Conflict, asFloat.StatusCode);

        // Same key, different command: refused, forever.
        var conflict = await ctx.PostAsync("edge-a", """{"tagId":"Line1.Speed","value":1300,"ttlSeconds":45}""", operator1, "k1");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", (await CommandTestKit.JsonAsync(conflict)).GetProperty("title").GetString());

        // Keys belong to a user: another operator's "k1" is a new command.
        var other = await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Operator"], sub: "operator-2"), "k1");
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);

        // Concurrent duplicates: both requests pass the lookup, then their inserts meet at the unique index.
        barrier.Arm(2);
        var racing = await Task.WhenAll(ctx.PostAsync("edge-a", Body, operator1, "k2"), ctx.PostAsync("edge-a", Body, operator1, "k2"));
        Assert.True(barrier.Released, "the two inserts did not overlap");
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Accepted], racing.Select(r => r.StatusCode).Order());
        var ids = await Task.WhenAll(racing.Select(async r => (await CommandTestKit.JsonAsync(r)).GetProperty("commandId").GetGuid()));
        Assert.Equal(ids[0], ids[1]);

        // k1 once, operator-2's k1 once, k2 once.
        Assert.Equal(3, ctx.PublishedCommands().Count);
        Assert.Equal(3, await ctx.CountRowsAsync());
    }

    [Fact]
    public async Task BrokerDown_NotSent_NeverPublished()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);
        var token = ctx.Host.CreateToken(["Operator"]);

        await ctx.Broker.StopServerAsync();
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(!ctx.Connection.IsConnected), "connection lost");

        var refused = await ctx.PostAsync("edge-a", Body, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        var problem = await CommandTestKit.JsonAsync(refused);
        Assert.Equal("broker_unavailable", problem.GetProperty("title").GetString());
        var notSent = problem.GetProperty("commandId").GetGuid();
        Assert.Equal(CommandStatus.NotSent, (await ctx.RowAsync(notSent)).Status);

        // After the broker returns, only new commands are published.
        await ctx.Broker.StartServerAsync();
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(ctx.Connection.IsConnected), "reconnected");
        var later = await ctx.PostAsync("edge-a", Body, token);
        Assert.Equal(HttpStatusCode.Accepted, later.StatusCode);
        var laterId = (await CommandTestKit.JsonAsync(later)).GetProperty("commandId").GetGuid();

        await Task.Delay(500);
        Assert.Equal([laterId], ctx.PublishedCommands().Select(p => p.Command.CommandId));
        Assert.Equal(CommandStatus.NotSent, (await ctx.RowAsync(notSent)).Status);
    }

    [Fact]
    public async Task UnconfirmedPublish_PublishUnknown()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);
        ctx.Broker.RejectPublish = m => m.Topic.EndsWith("/commands", StringComparison.Ordinal);

        var response = await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Operator"]));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await CommandTestKit.JsonAsync(response);
        Assert.Equal("publish_unconfirmed", problem.GetProperty("title").GetString());
        var id = problem.GetProperty("commandId").GetGuid();
        Assert.Equal($"/api/v1/commands/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(CommandStatus.PublishUnknown, (await ctx.RowAsync(id)).Status);

        // The command may have arrived after all: the Edge's answer is recorded.
        await ctx.InjectResultAsync("edge-a", "edge-a", id, CommandOutcome.Applied);
        await CommandTestKit.WaitUntilAsync(async () => (await ctx.RowAsync(id)).Status == "applied", "result recorded");
    }
}

/// <summary>Host, clients, fakes and probes shared by the command tests.</summary>
internal static class CommandTestKit
{
    public static async Task<Context> StartAsync(
        PostgresFixture database,
        Microsoft.Extensions.Time.Testing.FakeTimeProvider? clock = null,
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
        await connection.FirstAttemptCompleted.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(connection.IsConnected);
        return new Context(host, client, connection);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), $"timed out waiting for: {what}");
            await Task.Delay(50);
        }
    }

    public sealed record Context(CloudTestHost Host, HttpClient Client, CloudMqttConnection Connection) : IAsyncDisposable
    {
        public TestMqttBroker Broker => Host.Broker;

        public async Task<HttpResponseMessage> PostAsync(string edgeId, string body, string? token, string? idempotencyKey = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/edges/{edgeId}/commands")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (idempotencyKey is not null)
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }

            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await Client.SendAsync(request);
        }

        /// <summary>Commands the Cloud published, as the broker received them.</summary>
        public List<(string Topic, DeviceCommand Command)> PublishedCommands() =>
            Broker.Received
                .Where(m => m.Topic.EndsWith("/commands", StringComparison.Ordinal))
                .Select(m => (m.Topic, ContractJson.Deserialize<DeviceCommand>(Encoding.UTF8.GetString(m.Payload.ToArray()))))
                .ToList();

        /// <summary>Publishes a result as an Edge would, on <paramref name="topicEdgeId"/>'s topic.</summary>
        public Task InjectResultAsync(string topicEdgeId, string payloadEdgeId, Guid commandId, CommandOutcome outcome, string? reason = null) =>
            Broker.PublishAsync($"iiot/{topicEdgeId}/command-results", ContractJson.Serialize(new CommandResult
            {
                SchemaVersion = ContractJson.SchemaVersion,
                CommandId = commandId,
                EdgeId = payloadEdgeId,
                Outcome = outcome,
                Reason = reason,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            }));

        public async Task<CommandRow> RowAsync(Guid commandId)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Commands.AsNoTracking().SingleAsync(c => c.CommandId == commandId);
        }

        public async Task<int> CountRowsAsync()
        {
            await using var scope = Host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Commands.CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
        }
    }

    /// <summary>
    /// An Edge: a real MQTT client that receives its commands and answers each with the outcome chosen by
    /// <c>answer</c> (no answer when it returns null).
    /// </summary>
    public sealed class FakeEdge : IAsyncDisposable
    {
        private readonly IMqttClient _client;
        private readonly ConcurrentQueue<DeviceCommand> _received = new();

        private FakeEdge(IMqttClient client) => _client = client;

        public IReadOnlyList<DeviceCommand> Received => [.. _received];

        public static async Task<FakeEdge> StartAsync(TestMqttBroker broker, string edgeId, Func<DeviceCommand, CommandOutcome?> answer)
        {
            var client = new MqttClientFactory().CreateMqttClient();
            var edge = new FakeEdge(client);
            client.ApplicationMessageReceivedAsync += async e =>
            {
                var command = ContractJson.Deserialize<DeviceCommand>(Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray()));
                edge._received.Enqueue(command);
                if (answer(command) is { } outcome)
                {
                    await client.PublishAsync(new MqttApplicationMessageBuilder()
                        .WithTopic($"iiot/{edgeId}/command-results")
                        .WithPayload(ContractJson.Serialize(new CommandResult
                        {
                            SchemaVersion = ContractJson.SchemaVersion,
                            CommandId = command.CommandId,
                            EdgeId = edgeId,
                            Outcome = outcome,
                            CompletedAtUtc = DateTimeOffset.UtcNow,
                        }))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build());
                }
            };

            await client.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer("localhost", broker.Port)
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
                .WithClientId("fake-" + edgeId)
                .WithCredentials(broker.Username, broker.Password)
                // Test-only: the broker's certificate comes from a CA generated for this run.
                .WithTlsOptions(tls => tls.UseTls(true).WithCertificateValidationHandler(_ => true))
                .Build());
            await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic($"iiot/{edgeId}/commands").WithAtLeastOnceQoS())
                .Build());
            return edge;
        }

        public async ValueTask DisposeAsync()
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync();
            }

            _client.Dispose();
        }
    }

    /// <summary>
    /// When armed for N, holds every <c>INSERT INTO commands</c> until N of them are waiting, so concurrent
    /// requests are guaranteed to meet at the database instead of merely being sent at the same time.
    /// </summary>
    public sealed class InsertBarrier : DbCommandInterceptor
    {
        private TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _expected;
        private int _arrived;

        public bool Released => _all.Task.IsCompletedSuccessfully;

        public void Arm(int count)
        {
            _all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _arrived = 0;
            Volatile.Write(ref _expected, count);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await WaitAsync(command).ConfigureAwait(false);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await WaitAsync(command).ConfigureAwait(false);
            return result;
        }

        private async Task WaitAsync(DbCommand command)
        {
            var expected = Volatile.Read(ref _expected);
            if (expected == 0 || !command.CommandText.Contains("INSERT INTO commands", StringComparison.Ordinal))
            {
                return;
            }

            if (Interlocked.Increment(ref _arrived) >= expected)
            {
                Volatile.Write(ref _expected, 0);
                _all.TrySetResult();
            }

            await _all.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    public sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

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
