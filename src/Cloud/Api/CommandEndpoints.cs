using System.Globalization;
using System.Text.Json;
using IndustrialIot.Cloud.Commands;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Security;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Api;

/// <summary>Answer to an accepted (or replayed) command request.</summary>
public sealed record CommandAccepted(Guid CommandId, string Status, DateTime ExpiresAtUtc);

/// <summary>A stored command with its current status.</summary>
public sealed record CommandDetail(
    Guid CommandId,
    string EdgeId,
    string TagId,
    JsonElement Value,
    string Status,
    string? Reason,
    string IssuedBy,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? CompletedAtUtc);

/// <summary>
/// <c>POST /api/v1/edges/{edgeId}/commands</c> (Operator), <c>GET /api/v1/commands/{commandId}</c> and
/// <c>GET /api/v1/edges/{edgeId}/commands</c> (Viewer).
/// </summary>
internal sealed class CommandEndpoints : IEndpointModule
{
    private const int MaxBodyBytes = 16 * 1024;
    private const int MaxIdempotencyKeyLength = 128;

    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        // {"value":1,"value":2} is ambiguous: reject rather than pick one.
        AllowDuplicateProperties = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
    };

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/edges/{edgeId}/commands", PostAsync).RequireAuthorization(Policies.Operator);
        endpoints.MapGet("/api/v1/commands/{commandId:guid}", GetAsync).RequireAuthorization(Policies.Viewer);
        endpoints.MapGet("/api/v1/edges/{edgeId}/commands", ListAsync).RequireAuthorization(Policies.Viewer);
    }

    private static async Task<IResult> PostAsync(
        string edgeId,
        HttpContext http,
        IOptions<EdgesOptions> edges,
        CommandService commands,
        CancellationToken ct)
    {
        if (!edges.Value.IsAllowed(edgeId))
        {
            return Results.Problem(title: "edge_not_found", statusCode: StatusCodes.Status404NotFound);
        }

        string? idempotencyKey = null;
        if (http.Request.Headers.TryGetValue("Idempotency-Key", out var keys))
        {
            if (keys.Count != 1 || !IsValidKey(keys[0]))
            {
                return Invalid("Idempotency-Key", "must be one value of 1..128 printable ASCII characters");
            }

            idempotencyKey = keys[0];
        }

        if (await ReadBodyAsync(http.Request, ct).ConfigureAwait(false) is not { } request)
        {
            return Invalid("body", "must be a JSON object {\"tagId\",\"value\",\"ttlSeconds\"} of at most 16 KiB");
        }

        // The Operator policy guarantees a valid sub.
        var issuedBy = http.User.FindFirst(Policies.SubjectClaim)!.Value;
        var result = await commands.IssueAsync(edgeId, request, issuedBy, idempotencyKey, ct).ConfigureAwait(false);

        switch (result.Kind)
        {
            case IssueKind.EdgeNotFound:
                return Results.Problem(title: "edge_not_found", statusCode: StatusCodes.Status404NotFound);
            case IssueKind.Invalid:
                return Invalid(result.Field!, result.Rule!);
            case IssueKind.IdempotencyConflict:
                return Results.Problem(
                    title: "idempotency_conflict",
                    detail: "Idempotency-Key: already used for a different command",
                    statusCode: StatusCodes.Status409Conflict);
        }

        var command = result.Command!;
        var location = $"/api/v1/commands/{command.CommandId}";
        var accepted = new CommandAccepted(command.CommandId, command.Status, command.ExpiresAtUtc.UtcDateTime);
        switch (result.Kind)
        {
            case IssueKind.Replayed:
                http.Response.Headers.Location = location;
                return Results.Ok(accepted);
            case IssueKind.Accepted:
                return Results.Accepted(location, accepted);
            case IssueKind.NotSent:
                return Results.Problem(
                    title: "broker_unavailable",
                    detail: "The command was not sent and will not be sent; issue a new command when the broker is back.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?> { ["commandId"] = command.CommandId });
            default:
                // It may have reached the Edge: the client must check this command before issuing another one.
                http.Response.Headers.Location = location;
                return Results.Problem(
                    title: "publish_unconfirmed",
                    detail: "The command may have reached the Edge; check its status before issuing a new command.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?> { ["commandId"] = command.CommandId });
        }
    }

    private static async Task<IResult> GetAsync(Guid commandId, CommandService commands, CancellationToken ct) =>
        await commands.FindAsync(commandId, ct).ConfigureAwait(false) is { } command
            ? Results.Ok(Detail(command))
            : Results.Problem(title: "command_not_found", statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> ListAsync(
        string edgeId,
        HttpRequest request,
        IOptions<EdgesOptions> edges,
        CommandService commands,
        CancellationToken ct)
    {
        if (!edges.Value.IsAllowed(edgeId))
        {
            return Results.Problem(title: "edge_not_found", statusCode: StatusCodes.Status404NotFound);
        }

        var limit = 50;
        if (request.Query.TryGetValue("limit", out var values)
            && (values.Count != 1
                || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > 500))
        {
            return Invalid("limit", "must be an integer within 1..500");
        }

        var rows = await commands.ListAsync(edgeId, limit, ct).ConfigureAwait(false);
        return Results.Ok(rows.Select(Detail).ToList());
    }

    private static async Task<CommandRequest?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        // Read at most one byte past the limit: a body without Content-Length cannot make us buffer more.
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        if (length > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Deserialize<CommandRequest>(BodyOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValidKey(string? key) =>
        key is { Length: > 0 and <= MaxIdempotencyKeyLength } && key.All(c => c is >= ' ' and <= '~');

    private static CommandDetail Detail(CommandRow c)
    {
        using var value = JsonDocument.Parse(c.TargetValue);
        return new CommandDetail(
            c.CommandId,
            c.EdgeId,
            c.TagId,
            value.RootElement.Clone(),
            c.Status,
            c.Reason,
            c.IssuedBy,
            c.IssuedAtUtc.UtcDateTime,
            c.ExpiresAtUtc.UtcDateTime,
            c.CompletedAtUtc?.UtcDateTime);
    }

    // The detail names the field and the rule; submitted values are never echoed back.
    private static IResult Invalid(string field, string rule) =>
        Results.Problem(title: "invalid_request", detail: $"{field}: {rule}", statusCode: StatusCodes.Status400BadRequest);
}
