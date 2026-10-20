using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IndustrialIot.Cloud.Tests;

[Collection(CloudCollection.Name)]
public class AuthTests(PostgresFixture database)
{
    private static readonly string[] ViewerRoles = ["Viewer"];
    private static readonly string[] OperatorRoles = ["Operator"];

    [Theory]
    [InlineData("none")]
    [InlineData("bad-signature")]
    [InlineData("hs256")]
    [InlineData("id-token")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("expired")]
    public async Task InvalidTokens_Return401(string defect)
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();
        var token = defect switch
        {
            "none" => null,
            "bad-signature" => host.CreateToken(ViewerRoles, adjust: d => d.SigningCredentials =
                new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256)),
            "hs256" => host.CreateToken(ViewerRoles, adjust: d => d.SigningCredentials =
                new SigningCredentials(host.SymmetricKey, SecurityAlgorithms.HmacSha256)),
            "id-token" => host.CreateToken(ViewerRoles, adjust: d => d.TokenType = "id+jwt"),
            "wrong-issuer" => host.CreateToken(ViewerRoles, adjust: d => d.Issuer = "https://other-idp.test/"),
            "wrong-audience" => host.CreateToken(ViewerRoles, adjust: d => d.Audience = "some-client-id"),
            "expired" => host.CreateToken(ViewerRoles, adjust: d =>
            {
                // Expired one minute ago: beyond the 30 s clock skew.
                d.IssuedAt = d.NotBefore = DateTime.UtcNow.AddMinutes(-11);
                d.Expires = DateTime.UtcNow.AddMinutes(-1);
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        var response = await GetAsync(client, "/api/v1/edges", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
        var body = await AssertProblemAsync(response, "unauthorized");
        if (token is not null)
        {
            Assert.DoesNotContain(token.Split('.')[1], body, StringComparison.Ordinal); // no token contents echoed
        }

        // Control: the same request with a valid token is accepted.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/v1/edges", host.CreateToken(ViewerRoles))).StatusCode);
    }

    [Fact]
    public async Task WrongRole_Returns403()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        var viewerOnOperator = await GetAsync(client, "/test/operator", host.CreateToken(ViewerRoles));
        Assert.Equal(HttpStatusCode.Forbidden, viewerOnOperator.StatusCode);
        await AssertProblemAsync(viewerOnOperator, "forbidden");

        var noRoleOnViewer = await GetAsync(client, "/api/v1/edges", host.CreateToken([]));
        Assert.Equal(HttpStatusCode.Forbidden, noRoleOnViewer.StatusCode);
        await AssertProblemAsync(noRoleOnViewer, "forbidden");

        // Operators may read too; and the Operator endpoint accepts an Operator.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/v1/edges", host.CreateToken(OperatorRoles))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/test/operator", host.CreateToken(OperatorRoles))).StatusCode);
    }

    [Fact]
    public async Task OperatorWithoutValidSub_Returns403()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        foreach (var sub in new string?[] { null, new('u', 200), "user\u0001", " " })
        {
            var response = await GetAsync(client, "/test/operator", host.CreateToken(OperatorRoles, sub));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"sub {(sub is null ? "missing" : $"of length {sub.Length}")} gave {response.StatusCode}");
            await AssertProblemAsync(response, "forbidden");
        }

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/test/operator", host.CreateToken(OperatorRoles, new string('u', 128)))).StatusCode);
    }

    [Fact]
    public async Task Fallback_RequiresAuthentication()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        // An endpoint that declares no policy at all is still protected.
        var anonymous = await GetAsync(client, "/test/open", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await AssertProblemAsync(anonymous, "unauthorized");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/test/open", host.CreateToken([]))).StatusCode);
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/health/live", null)).StatusCode);
        await WaitUntilAsync(async () => (await GetAsync(client, "/health/ready", null)).StatusCode == HttpStatusCode.OK, "ready without a token");
    }

    [Fact]
    public async Task UnhandledError_Returns500Problem()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        var response = await GetAsync(client, "/test/boom", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await AssertProblemAsync(response, "internal_error");
        Assert.DoesNotContain("secret-detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardedHeaders_OnlyFromKnownProxies()
    {
        // No proxy configured (the shipped default): nobody's headers are trusted.
        await using (var unconfigured = new CloudTestHost(database))
        {
            unconfigured.CreateClient().Dispose();
            Assert.Equal("http", await SchemeSeenAsync(unconfigured, IPAddress.Parse("10.0.0.6")));
            Assert.Equal("http", await SchemeSeenAsync(unconfigured, IPAddress.Loopback));
        }

        await using var host = new CloudTestHost(database, new Dictionary<string, string?>
        {
            ["Cloud:ForwardedHeaders:KnownProxies:0"] = "10.0.0.5",
        });
        host.CreateClient().Dispose();

        // TestServer leaves the remote address null; set it explicitly. Loopback (both families) is not a configured proxy.
        Assert.Equal("http", await SchemeSeenAsync(host, IPAddress.Loopback));
        Assert.Equal("http", await SchemeSeenAsync(host, IPAddress.IPv6Loopback));
        Assert.Equal("http", await SchemeSeenAsync(host, IPAddress.Parse("10.0.0.6")));
        Assert.Equal("https", await SchemeSeenAsync(host, IPAddress.Parse("10.0.0.5")));
    }

    [Fact]
    public async Task Edges_ListsAllowListOnly()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();
        var last = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            db.Telemetry.AddRange(
                Row("edge-a", 1, last.AddHours(-1)),
                Row("edge-a", 2, last),
                Row("edge-x", 1, last.AddHours(1))); // stored earlier, no longer allow-listed
            await db.SaveChangesAsync();
        }

        var response = await GetAsync(client, "/api/v1/edges", host.CreateToken(ViewerRoles));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edges = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
        Assert.Equal(["edge-a", "edge-b"], edges.Select(e => e.GetProperty("edgeId").GetString()));
        Assert.Equal(["Edge A", "Edge B"], edges.Select(e => e.GetProperty("displayName").GetString()));
        Assert.Equal(last, edges[0].GetProperty("lastTelemetryUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, edges[1].GetProperty("lastTelemetryUtc").ValueKind);

        // The per-Edge MAX stays bounded only with this index in the migrated schema.
        await using var check = host.Services.CreateAsyncScope();
        var indexes = await check.ServiceProvider.GetRequiredService<CloudDbContext>().Database
            .SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_telemetry_edge_received'")
            .ToListAsync();
        Assert.Contains("(edge_id, received_utc)", Assert.Single(indexes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Cloud:Auth:Authority", "http://idp.test/", "Cloud:Auth:Authority")]
    [InlineData("Cloud:Auth:Authority", "", "Cloud:Auth:Authority")]
    [InlineData("Cloud:Auth:Audience", "", "Cloud:Auth:Audience")]
    [InlineData("Cloud:Auth:ClockSkewSeconds", "301", "Cloud:Auth:ClockSkewSeconds")]
    public async Task InvalidAuthConfig_FailsAtStart(string key, string value, string expectedKey)
    {
        await using var host = new CloudTestHost(database, new Dictionary<string, string?> { [key] = value });

        var error = Record.Exception(() => host.CreateClient());

        var validation = Find<OptionsValidationException>(error);
        Assert.NotNull(validation);
        Assert.Contains(expectedKey, validation.Message, StringComparison.Ordinal);
    }

    private static TelemetryRow Row(string edgeId, long sequence, DateTimeOffset received) => new()
    {
        EdgeId = edgeId,
        StreamId = Guid.NewGuid(),
        Sequence = sequence,
        TagId = "t1",
        Value = "1",
        Quality = "good",
        TimestampUtc = received,
        ReceivedUtc = received,
    };

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> AssertProblemAsync(HttpResponseMessage response, string title)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(title, problem.GetProperty("title").GetString());
        Assert.Equal((int)response.StatusCode, problem.GetProperty("status").GetInt32());
        return body;
    }

    private static async Task<string> SchemeSeenAsync(CloudTestHost host, IPAddress remote)
    {
        var context = await host.Server.SendAsync(http =>
        {
            http.Request.Method = "GET";
            http.Request.Path = "/test/scheme";
            http.Request.Headers["X-Forwarded-Proto"] = "https";
            http.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
            http.Connection.RemoteIpAddress = remote;
        });
        Assert.Equal(200, context.Response.StatusCode);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
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
}
