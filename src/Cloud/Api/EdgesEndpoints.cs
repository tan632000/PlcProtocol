using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Api;

/// <summary>One allow-listed Edge as returned by <c>GET /api/v1/edges</c>.</summary>
public sealed record EdgeSummary(string EdgeId, string DisplayName, DateTimeOffset? LastTelemetryUtc);

/// <summary><c>GET /api/v1/edges</c>: the configured Edges with when the Cloud last received telemetry from each.</summary>
internal sealed class EdgesEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/edges", ListAsync).RequireAuthorization(Policies.Viewer);

    private static async Task<IResult> ListAsync(IOptions<EdgesOptions> edges, CloudDbContext db, CancellationToken ct)
    {
        var result = new List<EdgeSummary>();
        foreach (var edge in edges.Value.Edges.OrderBy(e => e.EdgeId, StringComparer.Ordinal))
        {
            // One MAX per Edge: the list is short, and each is a single probe of ix_telemetry_edge_received.
            var last = await db.Telemetry
                .Where(t => t.EdgeId == edge.EdgeId)
                .MaxAsync(t => (DateTimeOffset?)t.ReceivedUtc, ct)
                .ConfigureAwait(false);
            result.Add(new EdgeSummary(edge.EdgeId, edge.DisplayName ?? edge.EdgeId, last)); // display name is optional in config
        }

        return Results.Ok(result);
    }
}
