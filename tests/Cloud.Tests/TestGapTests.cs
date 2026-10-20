using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IndustrialIot.Cloud.Commands;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Ingestion;
using IndustrialIot.Cloud.Tests.Infrastructure;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace IndustrialIot.Cloud.Tests;

/// <summary>Probes for behaviour the feature tasks implemented but did not prove completely.</summary>
[Collection(CloudCollection.Name)]
public class TestGapTests(PostgresFixture database)
{
    private const string Body = """{"tagId":"Line1.Speed","value":1200,"ttlSeconds":45}""";

    [Fact]
    public async Task PubackTimeout_PublishUnknown()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);

        // The broker takes the command but answers later than the Cloud's 10 s publish timeout.
        ctx.Broker.Server.InterceptingPublishAsync += async e =>
        {
            if (e.ClientId == "iiot-cloud" && e.ApplicationMessage.Topic.EndsWith("/commands", StringComparison.Ordinal))
            {
                await Task.Delay(TimeSpan.FromSeconds(12));
            }
        };

        var response = await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Operator"]));

        // It may well reach the Edge: never reported as "certainly not sent".
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await CommandTestKit.JsonAsync(response);
        Assert.Equal("publish_unconfirmed", problem.GetProperty("title").GetString());
        Assert.Equal(CommandStatus.PublishUnknown, (await ctx.RowAsync(problem.GetProperty("commandId").GetGuid())).Status);
    }

    [Fact]
    public async Task DisconnectDuringPublish_PublishUnknown()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);

        // The connection drops after the PUBLISH reached the broker and before any PUBACK.
        ctx.Broker.Server.InterceptingPublishAsync += e =>
        {
            if (e.ClientId == "iiot-cloud" && e.ApplicationMessage.Topic.EndsWith("/commands", StringComparison.Ordinal))
            {
                e.CloseConnection = true;
            }

            return Task.CompletedTask;
        };

        var response = await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Operator"]));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await CommandTestKit.JsonAsync(response);
        Assert.Equal("publish_unconfirmed", problem.GetProperty("title").GetString());
        Assert.Equal(CommandStatus.PublishUnknown, (await ctx.RowAsync(problem.GetProperty("commandId").GetGuid())).Status);
    }

    [Fact]
    public async Task WebSocket_QueryToken_AndExpiry()
    {
        await using var ctx = await CommandTestKit.StartAsync(database, overrides: new Dictionary<string, string?>
        {
            ["Cloud:Auth:ClockSkewSeconds"] = "0",
        });
        var token = ctx.Host.CreateToken(["Viewer"], adjust: d => d.Expires = DateTime.UtcNow.AddSeconds(6));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // WebSockets only, as a browser connects: a browser cannot set headers on a WebSocket, so the token travels
        // as ?access_token= (the .NET client would send a header instead, so the URL is built here like a browser's).
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(ctx.Host.Server.BaseAddress, "hubs/live"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => ctx.Host.Server.CreateHandler();
                options.WebSocketFactory = async (context, ct) =>
                {
                    var url = new Uri(context.Uri + "&access_token=" + Uri.EscapeDataString(token));
                    return await ctx.Host.Server.CreateWebSocketClient().ConnectAsync(url, ct);
                };
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await hub.StartAsync();
        await hub.InvokeAsync("JoinEdge", "edge-a");

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HubConnectionState.Disconnected, hub.State);
    }

    [Fact]
    public async Task OidcMetadata_FetchedOverHttps()
    {
        var idp = new IdentityProviderHandler();
        await using var host = new CloudTestHost(
            database,
            configureServices: services => services.Configure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme, jwt => jwt.BackchannelHttpHandler = idp),
            staticMetadata: false);
        idp.SigningKey = host.SigningKey;
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/v1/edges", host.CreateToken(["Viewer"]))).StatusCode);
        var otherKey = new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test-key" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/api/v1/edges",
            host.CreateToken(["Viewer"], adjust: d => d.SigningCredentials = new SigningCredentials(otherKey, SecurityAlgorithms.RsaSha256)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/api/v1/edges",
            host.CreateToken(["Viewer"], adjust: d => d.Issuer = "https://other-idp.test/"))).StatusCode);

        // Issuer and keys really came from the authority's metadata, over HTTPS.
        Assert.Contains("https://idp.test/.well-known/openid-configuration", idp.Requests);
        Assert.Contains("https://idp.test/jwks", idp.Requests);
    }

    [Fact]
    public async Task ResultDuringDatabaseOutage_RetriedInPlace()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var ctx = await CommandTestKit.StartAsync(database, clock);
        var response = await ctx.PostAsync("edge-a", Body, ctx.Host.CreateToken(["Operator"]));
        var id = (await CommandTestKit.JsonAsync(response)).GetProperty("commandId").GetGuid();
        var attempts = ctx.Broker.ConnectAttempts.Count;

        ctx.Host.DatabaseFaults.Enabled = true;
        var commands = ctx.Host.DatabaseFaults.CommandCount;
        await ctx.InjectResultAsync("edge-a", "edge-a", id, CommandOutcome.Applied);
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(ctx.Host.DatabaseFaults.CommandCount > commands), "first store attempt");

        for (var moved = TimeSpan.Zero; moved < TimeSpan.FromSeconds(2); moved += TimeSpan.FromMilliseconds(250))
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(20);
        }

        ctx.Host.DatabaseFaults.Enabled = false;
        var deadline = Stopwatch.StartNew();
        while ((await ctx.RowAsync(id)).Status != "applied")
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "result not stored after the outage");
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(30);
        }

        // Recovered on the same session: the result was retried in place, not redelivered.
        Assert.Equal(attempts, ctx.Broker.ConnectAttempts.Count);
    }

    [Fact]
    public async Task Retention_MultipleBatches()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var ctx = await CommandTestKit.StartAsync(database, clock);
        var job = ctx.Host.Services.GetServices<IHostedService>().OfType<RetentionJob>().Single();
        var old = clock.GetUtcNow().AddDays(-100);
        var recent = clock.GetUtcNow().AddDays(-1);

        await using (var scope = ctx.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();

            // 2.5 batches of expired rows and three recent ones.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO telemetry (edge_id, stream_id, sequence, tag_id, value, quality, timestamp_utc, received_utc)
                SELECT 'edge-a', @stream, g, 't1', '1'::jsonb, 'good',
                       CASE WHEN g <= 25000 THEN @old ELSE @recent END,
                       CASE WHEN g <= 25000 THEN @old ELSE @recent END
                FROM generate_series(1, 25003) AS g
                """,
                [new NpgsqlParameter("stream", Guid.NewGuid()), new NpgsqlParameter("old", old), new NpgsqlParameter("recent", recent)]);
        }

        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(job.WaitsArmed >= 1), "job waiting");
        clock.Advance(TimeSpan.FromHours(1));
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(job.WaitsArmed >= 2), "run done");

        await using var check = ctx.Host.Services.CreateAsyncScope();
        var remaining = await check.ServiceProvider.GetRequiredService<CloudDbContext>().Telemetry.Select(t => t.Sequence).OrderBy(s => s).ToListAsync();
        Assert.Equal([25001L, 25002L, 25003L], remaining);
    }

    [Fact]
    public async Task LeaveEdge_StopsPushes()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);
        var received = new ConcurrentQueue<(string EdgeId, long Sequence)>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(ctx.Host.Server.BaseAddress, "hubs/live"), options =>
            {
                options.HttpMessageHandlerFactory = _ => ctx.Host.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                var token = ctx.Host.CreateToken(["Viewer"]);
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        hub.On<JsonElement>("telemetry", p => received.Enqueue((p.GetProperty("edgeId").GetString()!, p.GetProperty("sequence").GetInt64())));
        await hub.StartAsync();

        await hub.InvokeAsync("JoinEdge", "edge-a");
        await PublishTelemetryAsync(ctx, "edge-a", 1);
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(received.Count == 1), "joined edge pushed");

        await hub.InvokeAsync("LeaveEdge", "edge-a");
        await hub.InvokeAsync("JoinEdge", "edge-b");
        await PublishTelemetryAsync(ctx, "edge-a", 2);
        await PublishTelemetryAsync(ctx, "edge-b", 3); // handled after edge-a's 2: once it arrives, 2 would have too
        await CommandTestKit.WaitUntilAsync(() => Task.FromResult(received.Any(r => r.Sequence == 3)), "marker pushed");
        await Task.Delay(300);

        Assert.Equal([("edge-a", 1L), ("edge-b", 3L)], received);
    }

    [Fact]
    public async Task History_AuthAndMalformedTo()
    {
        await using var ctx = await CommandTestKit.StartAsync(database);
        const string path = "/api/v1/edges/edge-a/tags/t1/history?from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z";

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(ctx.Client, path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(ctx.Client, path, ctx.Host.CreateToken([]))).StatusCode);

        var malformed = await GetAsync(ctx.Client, "/api/v1/edges/edge-a/tags/t1/history?from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00", ctx.Host.CreateToken(["Viewer"]));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.StartsWith("to:", (await CommandTestKit.JsonAsync(malformed)).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static Task PublishTelemetryAsync(CommandTestKit.Context ctx, string edgeId, long sequence) =>
        ctx.Broker.PublishAsync($"iiot/{edgeId}/telemetry", ContractJson.Serialize(new TelemetryPayload
        {
            SchemaVersion = ContractJson.SchemaVersion,
            EdgeId = edgeId,
            StreamId = Guid.NewGuid(),
            Sequence = sequence,
            TagId = "t1",
            Value = JsonSerializer.SerializeToElement(sequence),
            Quality = TelemetryQuality.Good,
            TimestampUtc = DateTimeOffset.UtcNow,
        }));

    /// <summary>The IdP as seen over HTTPS: OIDC discovery and a JWKS with the test key's public half.</summary>
    private sealed class IdentityProviderHandler : HttpMessageHandler
    {
        public RsaSecurityKey? SigningKey { get; set; }

        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.GetLeftPart(UriPartial.Path);
            Requests.Enqueue(uri);
            string? json = uri switch
            {
                "https://idp.test/.well-known/openid-configuration" =>
                    """{"issuer":"https://idp.test/","jwks_uri":"https://idp.test/jwks"}""",
                "https://idp.test/jwks" => Jwks(),
                _ => null,
            };

            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        private string Jwks()
        {
            var parameters = SigningKey!.Rsa.ExportParameters(includePrivateParameters: false);
            return JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new { kty = "RSA", use = "sig", kid = SigningKey.KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) },
                },
            });
        }
    }
}
