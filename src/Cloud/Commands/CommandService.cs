using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Live;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IndustrialIot.Cloud.Commands;

/// <summary>A command as requested over HTTP, before validation.</summary>
public sealed record CommandRequest(string? TagId, JsonElement? Value, int? TtlSeconds);

/// <summary>What happened to one POST; the endpoint maps it to an HTTP response.</summary>
public enum IssueKind
{
    EdgeNotFound,
    Invalid,
    IdempotencyConflict,
    Replayed,
    Accepted,
    NotSent,
    PublishUnconfirmed,
}

public sealed record IssueResult(IssueKind Kind, CommandRow? Command = null, string? Field = null, string? Rule = null);

/// <summary>
/// Creates and publishes commands. Order matters: validate, store <c>pending</c>, publish, then record the
/// publish outcome with an update guarded on <c>pending</c> — so a crash leaves a row the sweeper can time
/// out, and an Edge result that arrives before the PUBACK is never overwritten.
/// </summary>
internal sealed class CommandService(
    CloudDbContext db,
    CloudMqttConnection mqtt,
    LiveNotifier live,
    IOptions<EdgesOptions> edges,
    IOptions<CommandOptions> options,
    TimeProvider clock,
    ILogger<CommandService> logger)
{
    private const string IdempotencyIndex = "ux_commands_issuer_key";

    public async Task<IssueResult> IssueAsync(string edgeId, CommandRequest request, string issuedBy, string? idempotencyKey, CancellationToken ct)
    {
        if (!edges.Value.IsAllowed(edgeId))
        {
            return new IssueResult(IssueKind.EdgeNotFound);
        }

        var ttlSeconds = request.TtlSeconds ?? options.Value.DefaultTtlSeconds;
        if (ttlSeconds < 5 || ttlSeconds > options.Value.MaxTtlSeconds)
        {
            return Invalid("ttlSeconds", $"must be within 5..{options.Value.MaxTtlSeconds}");
        }

        // Everything the Edge will check about the message itself is checked here, before anything is stored.
        var now = clock.GetUtcNow();
        DeviceCommand command;
        try
        {
            command = new DeviceCommand
            {
                SchemaVersion = ContractJson.SchemaVersion,
                CommandId = Guid.NewGuid(),
                EdgeId = edgeId,
                TagId = request.TagId!,
                // Exactly as written: the Edge rejects 1200.0 for an integer tag on purpose (a sign of a mapping bug).
                TargetValue = request.Value,
                IssuedAtUtc = now,
                ExpiresAtUtc = now.AddSeconds(ttlSeconds),
                IssuedBy = issuedBy,
            };
            command.Validate();
        }
        catch (ContractValidationException ex)
        {
            return Invalid(ex.Field ?? "body", "is invalid");
        }

        var targetValue = command.TargetValue!.Value.GetRawText();
        var requestHash = idempotencyKey is null ? null : Hash(edgeId, command.TagId, targetValue, ttlSeconds);

        if (idempotencyKey is not null && await FindByKeyAsync(issuedBy, idempotencyKey, ct).ConfigureAwait(false) is { } existing)
        {
            return Replay(existing, requestHash!);
        }

        var row = new CommandRow
        {
            CommandId = command.CommandId,
            EdgeId = edgeId,
            TagId = command.TagId,
            TargetValue = targetValue,
            IssuedAtUtc = command.IssuedAtUtc,
            ExpiresAtUtc = command.ExpiresAtUtc,
            IssuedBy = issuedBy,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            Status = CommandStatus.Pending,
            CreatedUtc = now,
        };
        db.Commands.Add(row);
        try
        {
            // Not cancellable: once committed the command must be published, whether or not the client is still there.
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (idempotencyKey is not null
                                           && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: IdempotencyIndex })
        {
            // A concurrent request with the same key won the insert: answer exactly as a sequential replay would.
            db.ChangeTracker.Clear();
            var winner = await FindByKeyAsync(issuedBy, idempotencyKey, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Idempotency key conflict without a stored command.");
            return Replay(winner, requestHash!);
        }

        // From here on the command exists: finish publishing and recording even if the client goes away.
        var outcome = await mqtt.PublishAsync($"iiot/{edgeId}/commands", ContractJson.Serialize(command), CancellationToken.None)
            .ConfigureAwait(false);
        var (status, kind) = outcome switch
        {
            PublishOutcome.Confirmed => (CommandStatus.Published, IssueKind.Accepted),
            PublishOutcome.NotConnected => (CommandStatus.NotSent, IssueKind.NotSent),
            _ => (CommandStatus.PublishUnknown, IssueKind.PublishUnconfirmed),
        };

        CommandRow current;
        var recorded = 0;
        try
        {
            // Guarded: an Edge result stored meanwhile (the PUBACK can arrive after the result) stays.
            recorded = await db.Commands
                .Where(c => c.CommandId == row.CommandId && c.Status == CommandStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status), CancellationToken.None)
                .ConfigureAwait(false);
            current = await db.Commands.AsNoTracking().SingleAsync(c => c.CommandId == row.CommandId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or TimeoutException or InvalidOperationException)
        {
            // The publish already happened: the caller must still learn the commandId and the publish outcome
            // (a 500 would invite a blind retry, i.e. a second actuation). The row stays `pending`; an Edge result
            // is still accepted from there, and otherwise the sweeper times it out.
            logger.LogError("Command {CommandId}: publish outcome {Outcome} could not be recorded ({Reason}).",
                row.CommandId, outcome, ex.GetBaseException().Message);
            row.Status = status;
            current = row;
        }

        // Audit trail: who asked for what — never the token, never the value.
        logger.LogInformation("Command {CommandId} for {EdgeId}/{TagId} issued by {IssuedBy}: {Outcome}, status {Status}.",
            row.CommandId, edgeId, row.TagId, issuedBy, outcome, current.Status);
        if (recorded == 1)
        {
            // Only a committed change is pushed; an early Edge result was already pushed by its ingestor.
            await live.CommandChangedAsync(row.CommandId).ConfigureAwait(false);
        }

        return new IssueResult(kind, current);
    }

    public Task<CommandRow?> FindAsync(Guid commandId, CancellationToken ct) =>
        db.Commands.AsNoTracking().SingleOrDefaultAsync(c => c.CommandId == commandId, ct);

    /// <summary>Newest first; uses <c>ix_commands_edge_created</c>.</summary>
    public Task<List<CommandRow>> ListAsync(string edgeId, int limit, CancellationToken ct) =>
        db.Commands.AsNoTracking()
            .Where(c => c.EdgeId == edgeId)
            .OrderByDescending(c => c.CreatedUtc)
            .ThenByDescending(c => c.CommandId)
            .Take(limit)
            .ToListAsync(ct);

    private Task<CommandRow?> FindByKeyAsync(string issuedBy, string key, CancellationToken ct) =>
        db.Commands.AsNoTracking().SingleOrDefaultAsync(c => c.IssuedBy == issuedBy && c.IdempotencyKey == key, ct);

    private IssueResult Replay(CommandRow existing, string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new IssueResult(IssueKind.IdempotencyConflict);
        }

        logger.LogInformation("Command {CommandId} replayed for its idempotency key by {IssuedBy}; not published again.",
            existing.CommandId, existing.IssuedBy);
        return new IssueResult(IssueKind.Replayed, existing);
    }

    private static IssueResult Invalid(string field, string rule) => new(IssueKind.Invalid, Field: field, Rule: rule);

    /// <summary>
    /// Hashes the value token as written (no whitespace: <c>GetRawText</c> of a scalar), not a normalized number:
    /// <c>1200</c> and <c>1200.0</c> are different commands for the Edge, so reusing a key across them is a conflict.
    /// </summary>
    private static string Hash(string edgeId, string tagId, string value, int ttlSeconds)
    {
        // Newline-separated: none of the parts can contain one (ids are restricted, a scalar token has no newline).
        var text = $"{edgeId}\n{tagId}\n{value}\n{ttlSeconds}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
