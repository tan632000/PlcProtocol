using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Api;

/// <summary>Newest value of one tag.</summary>
public sealed record LatestValue(string TagId, JsonElement? Value, string Quality, DateTime TimestampUtc);

/// <summary>One stored sample in a history response.</summary>
public sealed record HistoryItem(JsonElement? Value, string Quality, DateTime TimestampUtc, long Sequence);

/// <summary>History of one tag; <see cref="Truncated"/> = more samples exist in the range than were returned.</summary>
public sealed record HistoryPage(IReadOnlyList<HistoryItem> Items, bool Truncated);

/// <summary>
/// <c>GET /api/v1/edges/{edgeId}/tags/latest</c> and <c>.../tags/{tagId}/history</c> for Viewers.
/// History is bounded twice: at most 31 days and at most 10,000 rows per request.
/// </summary>
internal sealed partial class TelemetryEndpoints : IEndpointModule
{
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(31);
    private const int DefaultLimit = 1_000;
    private const int MaxLimit = 10_000;

    public void Map(IEndpointRouteBuilder endpoints)
    {
        var edges = endpoints.MapGroup("/api/v1/edges/{edgeId}/tags").RequireAuthorization(Policies.Viewer);
        edges.MapGet("/latest", LatestAsync);
        edges.MapGet("/{tagId}/history", HistoryAsync);
    }

    private static async Task<IResult> LatestAsync(string edgeId, IOptions<EdgesOptions> edges, CloudDbContext db, CancellationToken ct)
    {
        if (!edges.Value.IsAllowed(edgeId))
        {
            return EdgeNotFound();
        }

        // One row per tag (the primary key), so the result is as small as the Edge's tag list.
        var rows = await db.LatestValues.AsNoTracking().Where(l => l.EdgeId == edgeId).ToListAsync(ct).ConfigureAwait(false);

        // Sorted here, not in SQL: the database collation may not order like an ordinal comparison.
        return Results.Ok(rows
            .OrderBy(l => l.TagId, StringComparer.Ordinal)
            .Select(l => new LatestValue(l.TagId, Json(l.Value), l.Quality, l.TimestampUtc.UtcDateTime))
            .ToList());
    }

    private static async Task<IResult> HistoryAsync(
        string edgeId,
        string tagId,
        HttpRequest request,
        IOptions<EdgesOptions> edges,
        CloudDbContext db,
        CancellationToken ct)
    {
        if (!edges.Value.IsAllowed(edgeId))
        {
            return EdgeNotFound();
        }

        if (!TagIdPattern().IsMatch(tagId))
        {
            return Invalid("tagId", "must be 1..64 characters [A-Za-z0-9_.-]");
        }

        // Read by hand: framework binding would answer a missing or malformed value with a generic 400.
        if (!TryReadTimestamp(request, "from", out var from, out var error) || !TryReadTimestamp(request, "to", out var to, out error))
        {
            return error!;
        }

        if (from >= to)
        {
            return Invalid("from", "must be earlier than to");
        }

        if (to - from > MaxRange)
        {
            return Invalid("to", "the range from..to must be at most 31 days");
        }

        var limit = DefaultLimit;
        if (request.Query.TryGetValue("limit", out var limitValues)
            && (limitValues.Count != 1
                || !int.TryParse(limitValues[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > MaxLimit))
        {
            return Invalid("limit", "must be an integer within 1..10000");
        }

        // Half-open range [from, to): consecutive pages never return the boundary sample twice.
        // Walks ix_telemetry_edge_tag_time; one extra row tells whether the range holds more.
        var rows = await db.Telemetry.AsNoTracking()
            .Where(t => t.EdgeId == edgeId && t.TagId == tagId && t.TimestampUtc >= from && t.TimestampUtc < to)
            .OrderBy(t => t.TimestampUtc)
            .ThenBy(t => t.StreamId)
            .ThenBy(t => t.Sequence)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = rows
            .Take(limit)
            .Select(t => new HistoryItem(Json(t.Value), t.Quality, t.TimestampUtc.UtcDateTime, t.Sequence))
            .ToList();
        return Results.Ok(new HistoryPage(items, rows.Count > limit));
    }

    /// <summary>Exactly one value, ISO-8601 UTC with a literal <c>Z</c> (no offsets, no local times).</summary>
    private static bool TryReadTimestamp(HttpRequest request, string name, out DateTimeOffset value, out IResult? error)
    {
        value = default;
        error = null;
        if (!request.Query.TryGetValue(name, out var values) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            error = Invalid(name, "is required (ISO-8601 UTC ending with Z)");
            return false;
        }

        var text = values[0]!;
        if (!UtcTimestampPattern().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
        {
            error = Invalid(name, "must be ISO-8601 UTC ending with Z, e.g. 2026-10-13T08:00:00Z");
            return false;
        }

        return true;
    }

    private static JsonElement? Json(string? stored) => stored is null ? null : JsonDocument.Parse(stored).RootElement.Clone();

    // The detail names the parameter and the rule; the submitted value is never echoed back.
    private static IResult Invalid(string parameter, string rule) =>
        Results.Problem(title: "invalid_request", detail: $"{parameter}: {rule}", statusCode: StatusCodes.Status400BadRequest);

    private static IResult EdgeNotFound() =>
        Results.Problem(title: "edge_not_found", statusCode: StatusCodes.Status404NotFound);

    // The Shared contract's tagId rule (internal there).
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagIdPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z\z", RegexOptions.CultureInvariant)]
    private static partial Regex UtcTimestampPattern();
}
