using IndustrialIot.Cloud.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Live;

/// <summary>
/// <c>/hubs/live</c>: a Viewer joins the Edges it wants to watch and receives <c>telemetry</c> and
/// <c>commandUpdated</c> for those Edges only.
/// </summary>
[Authorize(Policy = Policies.Viewer)]
public sealed class LiveHub(IOptions<EdgesOptions> edges) : Hub
{
    public const string Path = "/hubs/live";

    public static string GroupOf(string edgeId) => "edge:" + edgeId;

    public Task JoinEdge(string edgeId)
    {
        // Only configured Edges: a group name must not become a way to probe or invent Edges.
        if (string.IsNullOrEmpty(edgeId) || !edges.Value.IsAllowed(edgeId))
        {
            throw new HubException("edge_not_found");
        }

        return Groups.AddToGroupAsync(Context.ConnectionId, GroupOf(edgeId));
    }

    public Task LeaveEdge(string edgeId) =>
        string.IsNullOrEmpty(edgeId) ? Task.CompletedTask : Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupOf(edgeId));
}
