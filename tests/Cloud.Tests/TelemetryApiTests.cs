using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IndustrialIot.Cloud.Tests;

[Collection(CloudCollection.Name)]
public class TelemetryApiTests(PostgresFixture database)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Stream = Guid.NewGuid();

    [Fact]
    public async Task Latest_OnePerTag()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();
        await SeedAsync(host, db => db.LatestValues.AddRange(
            Latest("edge-a", "b", "21.5", "good", T0),
            Latest("edge-a", "a", "true", "good", T0.AddSeconds(1)),
            Latest("edge-a", "Z", null, "bad", T0.AddSeconds(2)),
            Latest("edge-b", "a", "7", "good", T0)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/api/v1/edges/edge-a/tags/latest", null)).StatusCode);

        var response = await GetAsync(client, "/api/v1/edges/edge-a/tags/latest", host.CreateToken(["Viewer"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await JsonAsync(response)).EnumerateArray().ToList();
        Assert.Equal(["Z", "a", "b"], rows.Select(r => r.GetProperty("tagId").GetString())); // ordinal, this edge only
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("value").ValueKind);
        Assert.Equal("bad", rows[0].GetProperty("quality").GetString());
        Assert.Equal(JsonValueKind.True, rows[1].GetProperty("value").ValueKind);
        Assert.Equal(21.5, rows[2].GetProperty("value").GetDouble());
        Assert.Equal("2026-10-01T08:00:00Z", rows[2].GetProperty("timestampUtc").GetString());
    }

    [Fact]
    public async Task History_RangeOrderLimit()
    {
        var sql = new SqlCapture();
        await using var host = new CloudTestHost(
            database,
            new Dictionary<string, string?> { ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Information" },
            services => services.AddSingleton<ILoggerProvider>(sql));
        using var client = host.CreateClient();
        var token = host.CreateToken(["Viewer"]);

        // Inserted out of order; other tags and Edges share the time range.
        await SeedAsync(host, db =>
        {
            foreach (var minute in new[] { 7, 2, 9, 0, 5, 3, 8, 1, 6, 4 })
            {
                db.Telemetry.Add(Sample("edge-a", "t1", minute, T0.AddMinutes(minute)));
            }

            db.Telemetry.Add(Sample("edge-a", "t2", 100, T0.AddMinutes(3)));
            db.Telemetry.Add(Sample("edge-b", "t1", 200, T0.AddMinutes(3)));
        });

        var all = await HistoryAsync(client, token, "t1", "2026-10-01T08:02:00Z", "2026-10-01T08:08:00Z");
        Assert.Equal([2L, 3L, 4L, 5L, 6L, 7L], all.Sequences); // ascending, the sample at `to` excluded
        Assert.False(all.Truncated);
        Assert.Equal(2.0, all.Root.GetProperty("items")[0].GetProperty("value").GetDouble());
        Assert.Equal("2026-10-01T08:02:00Z", all.Root.GetProperty("items")[0].GetProperty("timestampUtc").GetString());

        var page = await HistoryAsync(client, token, "t1", "2026-10-01T08:02:00Z", "2026-10-01T08:08:00Z", "limit=3");
        Assert.Equal([2L, 3L, 4L], page.Sequences);
        Assert.True(page.Truncated);

        var exact = await HistoryAsync(client, token, "t1", "2026-10-01T08:02:00Z", "2026-10-01T08:08:00Z", "limit=6");
        Assert.Equal(6, exact.Sequences.Count);
        Assert.False(exact.Truncated); // exactly as many rows as the limit is not "more"

        var unknownTag = await HistoryAsync(client, token, "nothing", "2026-10-01T08:00:00Z", "2026-10-01T09:00:00Z");
        Assert.Empty(unknownTag.Sequences);

        // Order and limit must be applied by the database: in memory the results look the same, but the
        // whole range would be read (and the order would depend on whichever plan PostgreSQL picked).
        var history = sql.Commands.Where(c => c.Contains("FROM telemetry", StringComparison.Ordinal) && c.Contains("tag_id", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(history);
        Assert.All(history, c =>
        {
            Assert.Contains("ORDER BY t.timestamp_utc", c, StringComparison.Ordinal);
            Assert.Contains("LIMIT", c, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("t1", "to=2026-10-02T00:00:00Z", "from")]                                              // missing from
    [InlineData("t1", "from=2026-10-01T00:00:00&to=2026-10-02T00:00:00Z", "from")]                      // no Z
    [InlineData("t1", "from=2026-10-01T00:00:00%2B02:00&to=2026-10-02T00:00:00Z", "from")]              // offset instead of Z
    [InlineData("t1", "from=2026-10-01T00:00:00Z", "to")]                                              // missing to
    [InlineData("t1", "from=2026-10-02T00:00:00Z&to=2026-10-02T00:00:00Z", "from")]                    // from == to
    [InlineData("t1", "from=2026-10-03T00:00:00Z&to=2026-10-02T00:00:00Z", "from")]                    // from > to
    [InlineData("t1", "from=2026-09-01T00:00:00Z&to=2026-10-03T00:00:00Z", "to")]                      // 32 days
    [InlineData("t1", "from=2026-07-01T00:00:00Z&to=2026-09-29T00:00:00Z", "to")]                      // 90 days
    [InlineData("t1", "from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z&limit=0", "limit")]
    [InlineData("t1", "from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z&limit=10001", "limit")]
    [InlineData("t1", "from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z&limit=abc", "limit")]
    [InlineData("bad%20tag", "from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z", "tagId")]
    public async Task History_InvalidInput_400(string tag, string query, string parameter)
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        var response = await GetAsync(client, $"/api/v1/edges/edge-a/tags/{tag}/history?{query}", host.CreateToken(["Viewer"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await JsonAsync(response);
        Assert.Equal("invalid_request", problem.GetProperty("title").GetString());
        Assert.StartsWith(parameter + ":", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Control: exactly 31 days with the largest limit is accepted.
        var widest = await GetAsync(client, "/api/v1/edges/edge-a/tags/t1/history?from=2026-09-01T00:00:00Z&to=2026-10-02T00:00:00Z&limit=10000", host.CreateToken(["Viewer"]));
        Assert.Equal(HttpStatusCode.OK, widest.StatusCode);
    }

    [Fact]
    public async Task UnknownEdge_404()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();
        var token = host.CreateToken(["Viewer"]);
        await SeedAsync(host, db => db.LatestValues.Add(Latest("edge-x", "a", "1", "good", T0))); // data of a removed Edge

        foreach (var path in new[]
                 {
                     "/api/v1/edges/edge-x/tags/latest",
                     "/api/v1/edges/edge-x/tags/a/history?from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z",
                 })
        {
            var response = await GetAsync(client, path, token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("edge_not_found", (await JsonAsync(response)).GetProperty("title").GetString());
        }
    }

    private static LatestValueRow Latest(string edgeId, string tagId, string? value, string quality, DateTimeOffset timestamp) => new()
    {
        EdgeId = edgeId,
        TagId = tagId,
        Value = value,
        Quality = quality,
        TimestampUtc = timestamp,
        StreamId = Stream,
        Sequence = 1,
    };

    private static TelemetryRow Sample(string edgeId, string tagId, long sequence, DateTimeOffset timestamp) => new()
    {
        EdgeId = edgeId,
        StreamId = Stream,
        Sequence = sequence,
        TagId = tagId,
        Value = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Quality = "good",
        TimestampUtc = timestamp,
        ReceivedUtc = timestamp,
    };

    private static async Task SeedAsync(CloudTestHost host, Action<CloudDbContext> seed)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static async Task<(JsonElement Root, List<long> Sequences, bool Truncated)> HistoryAsync(
        HttpClient client, string token, string tag, string from, string to, string? extra = null)
    {
        var response = await GetAsync(client, $"/api/v1/edges/edge-a/tags/{tag}/history?from={from}&to={to}{(extra is null ? "" : "&" + extra)}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await JsonAsync(response);
        var sequences = root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sequence").GetInt64()).ToList();
        return (root, sequences, root.GetProperty("truncated").GetBoolean());
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Records the SQL text EF logs for each executed command (parameter values are not logged).</summary>
    private sealed class SqlCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public IReadOnlyList<string> Commands => [.. _commands];

        public ILogger CreateLogger(string categoryName) =>
            categoryName == "Microsoft.EntityFrameworkCore.Database.Command" ? new Logger(_commands) : NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> commands) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                commands.Enqueue(formatter(state, exception));
        }
    }
}
