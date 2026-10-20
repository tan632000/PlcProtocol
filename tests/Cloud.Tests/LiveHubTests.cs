using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IndustrialIot.Cloud.Commands;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Tests.Infrastructure;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IndustrialIot.Cloud.Tests;

[Collection(CloudCollection.Name)]
public class LiveHubTests(PostgresFixture database)
{
    private static readonly Guid Stream = Guid.NewGuid();

    [Fact]
    public async Task Telemetry_PushedToJoinedEdgeOnly()
    {
        // Commits are slowed down, so a push sent before its commit would reach the client while the row is invisible.
        await using var ctx = await CommandTestKit.StartAsync(database, configureServices: s => s.AddSingleton<IInterceptor>(new SlowCommit()));
        var received = new ConcurrentQueue<(JsonElement Payload, bool VisibleInDatabase)>();
        await using var hub = await ConnectAsync(ctx, ctx.Host.CreateToken(["Viewer"]));
        hub.On<JsonElement>("telemetry", async payload =>
        {
            var sequence = payload.GetProperty("sequence").GetInt64();
            var edgeId = payload.GetProperty("edgeId").GetString();
            await using var scope = ctx.Host.Services.CreateAsyncScope();
            var visible = await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Telemetry.AnyAsync(t => t.EdgeId == edgeId && t.Sequence == sequence);
            received.Enqueue((payload, visible));
        });
        await hub.InvokeAsync("JoinEdge", "edge-a");

        await PublishTelemetryAsync(ctx, "edge-a", 1);
        await PublishTelemetryAsync(ctx, "edge-b", 2);
        await PublishTelemetryAsync(ctx, "edge-a", 3); // handled after edge-b's: once it arrives, edge-b's would have too
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(received.Count >= 2), "edge-a telemetry pushed");
        await Task.Delay(300);

        Assert.Equal([1L, 3L], received.Select(r => r.Payload.GetProperty("sequence").GetInt64()));
        Assert.All(received, r => Assert.True(r.VisibleInDatabase, "pushed before the row was committed"));

        // The wire contract, not SignalR's default serialization (which would send the enum as a number).
        var first = received.First().Payload;
        Assert.Equal("edge-a", first.GetProperty("edgeId").GetString());
        Assert.Equal("good", first.GetProperty("quality").GetString());
        ContractJson.Deserialize<TelemetryPayload>(first.GetRawText());
    }

    [Fact]
    public async Task CommandUpdates_Pushed()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);
        var updates = new ConcurrentQueue<JsonElement>();
        await using var hub = await ConnectAsync(ctx, ctx.Host.CreateToken(["Viewer"]));
        hub.On<JsonElement>("commandUpdated", update => updates.Enqueue(update));
        await hub.InvokeAsync("JoinEdge", "edge-a");
        var operatorToken = ctx.Host.CreateToken(["Operator"]);

        // A command for another Edge first: its update must not reach this client.
        Assert.Equal(HttpStatusCode.Accepted, (await ctx.PostAsync("edge-b", """{"tagId":"t1","value":1}""", operatorToken)).StatusCode);
        var response = await ctx.PostAsync("edge-a", """{"tagId":"t1","value":1}""", operatorToken);
        var id = (await CommandTestKit.JsonAsync(response)).GetProperty("commandId").GetGuid();

        // One push per committed change: first the recorded publish ...
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(!updates.IsEmpty), "publish pushed");
        Assert.Equal(CommandStatus.Published, Assert.Single(updates).GetProperty("status").GetString());

        // ... then the Edge's outcome.
        await ctx.InjectResultAsync("edge-a", "edge-a", id, CommandOutcome.Applied);
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(updates.Count >= 2), "Edge outcome pushed");
        await Task.Delay(300);

        Assert.Equal(2, updates.Count);
        Assert.All(updates, u => Assert.Equal(id, u.GetProperty("commandId").GetGuid()));
        var applied = updates.Last();
        Assert.Equal("applied", applied.GetProperty("status").GetString());
        Assert.Equal("edge-a", applied.GetProperty("edgeId").GetString());
        Assert.Equal(1, applied.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task TimedOut_Pushed()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var ctx = await CommandTestKit.StartAsync(database, clock, new Dictionary<string, string?> { ["Cloud:Commands:TimeoutGraceSeconds"] = "0" });
        var sweeper = ctx.Host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Commands.CommandTimeoutSweeper>().Single();
        var updates = new ConcurrentQueue<JsonElement>();
        await using var hub = await ConnectAsync(ctx, ctx.Host.CreateToken(["Viewer"]));
        hub.On<JsonElement>("commandUpdated", update => updates.Enqueue(update));
        await hub.InvokeAsync("JoinEdge", "edge-a");
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(sweeper.WaitsArmed >= 1), "sweeper waiting");

        var response = await ctx.PostAsync("edge-a", """{"tagId":"t1","value":1,"ttlSeconds":5}""", ctx.Host.CreateToken(["Operator"]));
        var id = (await CommandTestKit.JsonAsync(response)).GetProperty("commandId").GetGuid();
        var armed = sweeper.WaitsArmed;
        clock.Advance(TimeSpan.FromSeconds(10));
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(sweeper.WaitsArmed > armed), "sweep done");

        await CommandTestKit.WaitUntilAsync(
            () => Task.FromResult(updates.Any(u => u.GetProperty("commandId").GetGuid() == id && u.GetProperty("status").GetString() == "timed_out")),
            "timeout pushed");
    }

    [Fact]
    public async Task Duplicate_NotPushedTwice()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);
        var sequences = new ConcurrentQueue<long>();
        await using var hub = await ConnectAsync(ctx, ctx.Host.CreateToken(["Viewer"]));
        hub.On<JsonElement>("telemetry", payload => sequences.Enqueue(payload.GetProperty("sequence").GetInt64()));
        await hub.InvokeAsync("JoinEdge", "edge-a");

        await PublishTelemetryAsync(ctx, "edge-a", 1);
        await PublishTelemetryAsync(ctx, "edge-a", 1); // redelivered by the Edge
        await PublishTelemetryAsync(ctx, "edge-a", 2);
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(sequences.Contains(2)), "marker pushed");
        await Task.Delay(300);

        Assert.Equal([1L, 2L], sequences);
    }

    [Fact]
    public async Task Unauthorized_Refused()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(ctx, token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NegotiateAsync(ctx, query: null, header: null)).StatusCode);
    }

    [Fact]
    public async Task NoViewerRole_Refused()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(ctx, ctx.Host.CreateToken([])));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task JoinUnknownEdge_Fails()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);
        await using var hub = await ConnectAsync(ctx, ctx.Host.CreateToken(["Viewer"]));

        var error = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinEdge", "edge-x"));
        Assert.Contains("edge_not_found", error.Message, StringComparison.Ordinal);

        // The connection is still usable for a configured Edge.
        await hub.InvokeAsync("JoinEdge", "edge-a");
    }

    [Fact]
    public async Task QueryToken_OnlyForHub()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);
        var token = ctx.Host.CreateToken(["Viewer"]);

        Assert.Equal(HttpStatusCode.OK, (await NegotiateAsync(ctx, query: token, header: null)).StatusCode);

        using var api = new HttpRequestMessage(HttpMethod.Get, "/api/v1/edges?access_token=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ctx.Client.SendAsync(api)).StatusCode);

        // Control: the same token in the header works on the API.
        Assert.Equal(HttpStatusCode.OK, (await ctx.GetAsync("/api/v1/edges", token)).StatusCode);
    }

    [Fact]
    public async Task TokenExpiry_ClosesConnection()
    {
        await using var ctx = await CommandTestKit.StartAsync(database, overrides: new Dictionary<string, string?>
        {
            ["Cloud:Auth:ClockSkewSeconds"] = "0",
        });
        var token = ctx.Host.CreateToken(["Viewer"], adjust: d => d.Expires = DateTime.UtcNow.AddSeconds(5));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = await ConnectAsync(ctx, token);
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await hub.InvokeAsync("JoinEdge", "edge-a");

        // Real time: SignalR's expiry check does not follow an injected clock.
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HubConnectionState.Disconnected, hub.State);
    }

    [Fact]
    public async Task QueryToken_NotLogged()
    {
        var logs = new CommandTestKit.LogCapture();
        await using var ctx = await CommandTestKit.StartAsync(database, configureServices: s => s.AddSingleton<ILoggerProvider>(logs));
        var token = ctx.Host.CreateToken(["Viewer"]);

        Assert.Equal(HttpStatusCode.OK, (await NegotiateAsync(ctx, query: token, header: null)).StatusCode);
        using var api = new HttpRequestMessage(HttpMethod.Get, "/api/v1/edges?access_token=" + Uri.EscapeDataString(token));
        await ctx.Client.SendAsync(api);

        Assert.NotEmpty(logs.Messages); // the capture is live
        var signature = token.Split('.')[2];
        Assert.DoesNotContain(logs.Messages, m => m.Contains(signature, StringComparison.Ordinal));
    }

    private static async Task<HubConnection> ConnectAsync(CommandTestKit.Context ctx, string? token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(ctx.Host.Server.BaseAddress, "hubs/live"), options =>
            {
                // Through the in-memory test server; long polling sends the token in the Authorization header.
                options.HttpMessageHandlerFactory = _ => ctx.Host.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();
        try
        {
            await connection.StartAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<HttpResponseMessage> NegotiateAsync(CommandTestKit.Context ctx, string? query, string? header)
    {
        var path = "/hubs/live/negotiate?negotiateVersion=1" + (query is null ? "" : "&access_token=" + Uri.EscapeDataString(query));
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (header is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", header);
        }

        return await ctx.Client.SendAsync(request);
    }

    private static Task PublishTelemetryAsync(CommandTestKit.Context ctx, string edgeId, long sequence) =>
        ctx.Broker.PublishAsync($"iiot/{edgeId}/telemetry", ContractJson.Serialize(new TelemetryPayload
        {
            SchemaVersion = ContractJson.SchemaVersion,
            EdgeId = edgeId,
            StreamId = Stream,
            Sequence = sequence,
            TagId = "t1",
            Value = JsonSerializer.SerializeToElement(sequence),
            Quality = TelemetryQuality.Good,
            TimestampUtc = DateTimeOffset.UtcNow,
        }));

    /// <summary>Delays every commit, widening the window in which an early push would be visible.</summary>
    private sealed class SlowCommit : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
