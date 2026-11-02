using System.Text.Json;
using System.Text.RegularExpressions;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Ingestion;
using IndustrialIot.Cloud.Security;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Api;

/// <summary>
/// The PLCs of one Edge as its latest inventory round reported them. <see cref="ReportedUtc"/> is when the Cloud received
/// that round; past 150 s the whole list is <see cref="Stale"/> and every state reads <c>stale</c>.
/// <see cref="CommandsEnabled"/> is true only when both the Edge and this Cloud accept commands.
/// </summary>
public sealed record DevicesResponse(string EdgeId, DateTime? ReportedUtc, bool Stale, bool? CommandsEnabled, IReadOnlyList<DeviceSummary> Devices);

/// <summary>One PLC. <see cref="State"/>: disabled, waiting, connected, unavailable or stale; <see cref="LastError"/>: null, unavailable or tagErrors.</summary>
public sealed record DeviceSummary(
    string Name, string Model, bool Enabled, bool WritesEnabled, string State, DateTime? LastGoodUtc, string? LastError, int TagCount);

/// <summary>
/// One tag of a PLC with its newest value (null fields when none arrived yet). <see cref="CanWrite"/>: this Cloud and the
/// Edge accept commands, the device is enabled with writes on, and the tag is writable.
/// </summary>
public sealed record TagDetail(
    string TagId,
    string Name,
    string DataType,
    int? Length,
    bool Writable,
    bool CanWrite,
    decimal? Min,
    decimal? Max,
    string? Description,
    JsonElement? Value,
    string? Quality,
    DateTime? TimestampUtc);

/// <summary><c>GET /api/v1/edges/{edgeId}/devices</c> and <c>.../devices/{device}/tags</c> for Viewers.</summary>
internal sealed partial class DevicesEndpoints : IEndpointModule
{
    /// <summary>An Edge whose latest round is older than this is stale.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(150);

    public void Map(IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/api/v1/edges/{edgeId}/devices").RequireAuthorization(Policies.Viewer);
        devices.MapGet("", ListAsync);
        devices.MapGet("/{device}/tags", TagsAsync);
    }

    /// <summary>The <c>/devices</c> body (also pushed as <c>devicesUpdated</c>) for <paramref name="row"/>, or for no inventory yet.</summary>
    internal static DevicesResponse ToResponse(string edgeId, EdgeInventoryRow? row, DateTimeOffset now, bool cloudCommandsEnabled)
    {
        if (row is null)
        {
            return new DevicesResponse(edgeId, null, Stale: true, null, []);
        }

        var document = Read(row);
        var stale = now - row.ReceivedUtc > StaleAfter;
        return new DevicesResponse(
            edgeId,
            row.ReceivedUtc.UtcDateTime,
            stale,
            cloudCommandsEnabled && document.CommandsEnabled,
            [.. document.Devices.Select(d => new DeviceSummary(
                d.Status.Name,
                d.Status.Model,
                d.Status.Enabled,
                d.Status.WritesEnabled,
                stale ? "stale" : Wire(d.Status.State),
                d.Status.LastGoodUtc?.UtcDateTime,
                d.Status.LastError is { } error ? Wire(error) : null,
                d.Status.TagCount))]);
    }

    private static async Task<IResult> ListAsync(
        string edgeId, IOptions<EdgesOptions> edges, IOptions<CommandOptions> commands, CloudDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!edges.Value.IsAllowed(edgeId))
        {
            return EdgeNotFound();
        }

        var row = await db.EdgeInventory.AsNoTracking().SingleOrDefaultAsync(r => r.EdgeId == edgeId, ct).ConfigureAwait(false);
        return Results.Ok(ToResponse(edgeId, row, clock.GetUtcNow(), commands.Value.Enabled));
    }

    private static async Task<IResult> TagsAsync(
        string edgeId, string device, IOptions<EdgesOptions> edges, IOptions<CommandOptions> commands, CloudDbContext db, CancellationToken ct)
    {
        if (!edges.Value.IsAllowed(edgeId))
        {
            return EdgeNotFound();
        }

        // The name never reaches a query or the response unless it matches a stored device.
        var row = DeviceNamePattern().IsMatch(device)
            ? await db.EdgeInventory.AsNoTracking().SingleOrDefaultAsync(r => r.EdgeId == edgeId, ct).ConfigureAwait(false)
            : null;
        var document = row is null ? null : Read(row);
        var match = document?.Devices.FirstOrDefault(d => string.Equals(d.Status.Name, device, StringComparison.OrdinalIgnoreCase));
        if (document is null || match is null)
        {
            return Results.Problem(title: "device_not_found", statusCode: StatusCodes.Status404NotFound);
        }

        var tagIds = match.Tags.Select(t => t.TagId).ToList();
        var latest = (await db.LatestValues.AsNoTracking()
                .Where(l => l.EdgeId == edgeId && tagIds.Contains(l.TagId))
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(l => l.TagId, StringComparer.Ordinal);

        var deviceWrites = commands.Value.Enabled && document.CommandsEnabled && match.Status.Enabled && match.Status.WritesEnabled;
        return Results.Ok(match.Tags.Select(t =>
        {
            var value = latest.GetValueOrDefault(t.TagId);
            return new TagDetail(
                t.TagId,
                t.Name,
                t.DataType,
                t.Length,
                t.Writable,
                deviceWrites && t.Writable,
                t.Min,
                t.Max,
                t.Description,
                value?.Value is { } json ? JsonDocument.Parse(json).RootElement.Clone() : null,
                value?.Quality,
                value?.TimestampUtc.UtcDateTime);
        }).ToList());
    }

    private static InventoryDocument Read(EdgeInventoryRow row) =>
        JsonSerializer.Deserialize<InventoryDocument>(row.Document, ContractJson.Options)
        ?? throw new InvalidOperationException($"The stored inventory of edge {row.EdgeId} is empty.");

    // The wire names of the contract enums (e.g. TagErrors → tagErrors), the same as in the inventory messages.
    private static string Wire<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static IResult EdgeNotFound() =>
        Results.Problem(title: "edge_not_found", statusCode: StatusCodes.Status404NotFound);

    // The inventory's device-name rule (Shared InventoryRules, internal there).
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNamePattern();
}
