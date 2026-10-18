using System.Collections.Concurrent;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Commands;

/// <summary>What to do with one delivered command message.</summary>
/// <param name="ResultJson">The <c>CommandResult</c> to publish; null when nothing is answered (malformed payload).</param>
/// <param name="Stored">The result is in the command log (publish success is then recorded there).</param>
/// <param name="Acknowledge">The message may be acknowledged; false leaves it for redelivery.</param>
internal sealed record CommandDisposition(string? CommandId, string? ResultJson, bool Stored, bool Acknowledge);

/// <summary>
/// Decides and executes remote write commands, one at a time. The checks run in a fixed order
/// (size/contract → edgeId → known result → clock rollback → freshness → catalog → log capacity); the
/// PLC is written only after a durable <c>started</c> record exists, and a command id that was already
/// processed is answered with its original result, never executed again.
/// </summary>
internal sealed class CommandExecutor(
    IPlcDriver driver,
    PlcTagCatalog catalog,
    CommandLog log,
    EdgeDatabase database,
    IOptions<EdgeOptions> edge,
    TimeProvider clock,
    ILogger<CommandExecutor> logger)
{
    public const int MaxPayloadBytes = 16 * 1024;

    private readonly string _edgeId = edge.Value.EdgeId;
    private readonly SemaphoreSlim _serial = new(1, 1);

    // Results whose PLC write finished but whose "completed" row could not be stored yet.
    private readonly ConcurrentDictionary<string, (string Result, long ExpiresUtcMs)> _unstored = new(StringComparer.Ordinal);

    /// <summary>Results waiting to be stored; the listener re-publishes and re-stores them.</summary>
    public IReadOnlyCollection<KeyValuePair<string, (string Result, long ExpiresUtcMs)>> UnstoredResults => _unstored;

    public async Task<CommandDisposition> ExecuteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // Serialize everything: a redelivery that arrives while the first copy is still writing waits here,
        // then finds the stored (or in-memory) result instead of a half-finished "started" row.
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DecideAndExecuteAsync(payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Tries again to store results kept in memory. Returns the ids that are now stored.</summary>
    public async Task<IReadOnlyList<string>> StoreUnstoredAsync(CancellationToken ct)
    {
        var stored = new List<string>();
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (id, (result, expires)) in _unstored)
            {
                if (await TryCompleteAsync(id, result, expires, ct).ConfigureAwait(false))
                {
                    _unstored.TryRemove(id, out _);
                    stored.Add(id);
                }
            }
        }
        finally
        {
            _serial.Release();
        }

        return stored;
    }

    private async Task<CommandDisposition> DecideAndExecuteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // 1. Size and contract.
        if (payload.Length > MaxPayloadBytes)
        {
            logger.LogWarning("Ignoring a command message of {Bytes} bytes (limit {Limit}).", payload.Length, MaxPayloadBytes);
            return new CommandDisposition(null, null, Stored: false, Acknowledge: true);
        }

        DeviceCommand command;
        try
        {
            command = ContractJson.Deserialize<DeviceCommand>(System.Text.Encoding.UTF8.GetString(payload.Span));
        }
        catch (ContractValidationException ex)
        {
            // The payload itself is never logged.
            logger.LogWarning("Ignoring an invalid command message: {Field}", ex.Field ?? "(json)");
            return new CommandDisposition(null, null, Stored: false, Acknowledge: true);
        }

        var id = command.CommandId.ToString("D");

        // 2. Addressed to another Edge.
        if (!string.Equals(command.EdgeId, _edgeId, StringComparison.Ordinal))
        {
            return NotStored(command, CommandOutcome.Rejected, CommandReasons.EdgeMismatch);
        }

        // 3.–6. Already processed: answer with the original outcome, never execute again.
        var known = await log.FindAsync(id, ct).ConfigureAwait(false);
        if (known is { Completed: true, Result: { } storedResult })
        {
            await log.MarkUnsentAsync(id, ct).ConfigureAwait(false);
            Log(command, "re-published stored result");
            return new CommandDisposition(id, storedResult, Stored: true, Acknowledge: true);
        }

        if (_unstored.TryGetValue(id, out var pending))
        {
            var nowStored = await TryCompleteAsync(id, pending.Result, pending.ExpiresUtcMs, ct).ConfigureAwait(false);
            if (nowStored)
            {
                _unstored.TryRemove(id, out _);
            }

            Log(command, "re-published in-memory result");
            return new CommandDisposition(id, pending.Result, Stored: nowStored, Acknowledge: nowStored);
        }

        // Kept until the command can no longer be fresh, never less. expiresAt ≤ issuedAt + 24 h by contract,
        // but issuedAt itself is unbounded, so a far-future command is remembered that long (known limitation;
        // only the Cloud identity may publish commands — broker ACL, wave 4).
        var expiresMs = command.ExpiresAtUtc.ToUnixTimeMilliseconds();
        if (known is { Completed: false })
        {
            // A previous process wrote (or was about to write) and never recorded the outcome.
            return await StoredAsync(command, expiresMs, CommandOutcome.Unknown, CommandReasons.WriteOutcomeUnknown, ct).ConfigureAwait(false);
        }

        // 7. The Edge clock went backwards: freshness cannot be trusted for new commands.
        if (database.IsClockRolledBack)
        {
            return NotStored(command, CommandOutcome.Rejected, CommandReasons.ClockSkew);
        }

        // 8.–9. Freshness against the Edge clock.
        switch (command.CheckFreshness(clock))
        {
            case CommandFreshness.Expired:
                return await StoredAsync(command, expiresMs, CommandOutcome.Expired, null, ct).ConfigureAwait(false);
            case CommandFreshness.ClockSkew:
                return await StoredAsync(command, expiresMs, CommandOutcome.Rejected, CommandReasons.ClockSkew, ct).ConfigureAwait(false);
        }

        // 10. The tag catalog decides writability, bounds and value type.
        var decision = catalog.EvaluateWrite(command.TagId, command.TargetValue!.Value);
        if (!decision.Allowed)
        {
            return await StoredAsync(command, expiresMs, CommandOutcome.Rejected, decision.Reason, ct).ConfigureAwait(false);
        }

        // 11.–12. Without a durable "started" record there is no write.
        try
        {
            if (!await log.HasCapacityAsync(ct).ConfigureAwait(false))
            {
                logger.LogWarning("Command log is full of unexpired entries; rejecting command {CommandId}.", id);
                return NotStored(command, CommandOutcome.Failed, CommandReasons.EdgeStorageError);
            }

            await log.InsertStartedAsync(id, expiresMs, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            logger.LogError(ex, "Could not record command {CommandId}; the PLC was not written.", id);
            return NotStored(command, CommandOutcome.Failed, CommandReasons.EdgeStorageError);
        }

        // 13. The write. From here on cancellation is ignored: the driver's own timeout bounds it, and a
        // write that may have reached the PLC must end as a known outcome, not as "cancelled".
        var (outcome, reason) = await WriteAsync(decision, CancellationToken.None).ConfigureAwait(false);
        var resultJson = Result(command, outcome, reason);
        Log(command, outcome, reason);

        if (await TryCompleteAsync(id, resultJson, expiresMs, CancellationToken.None).ConfigureAwait(false))
        {
            return new CommandDisposition(id, resultJson, Stored: true, Acknowledge: true);
        }

        // The PLC was written but the outcome is not on disk yet: keep it in memory, publish it anyway,
        // and leave the message unacknowledged until it is stored.
        _unstored[id] = (resultJson, expiresMs);
        return new CommandDisposition(id, resultJson, Stored: false, Acknowledge: false);
    }

    private async Task<(CommandOutcome Outcome, string? Reason)> WriteAsync(WriteDecision decision, CancellationToken ct)
    {
        var tag = decision.ResolvedTag!;
        try
        {
            await driver.WriteAsync(tag.Address, tag.DataType, decision.NormalizedValue!, ct).ConfigureAwait(false);
            return (CommandOutcome.Applied, null);
        }
        catch (PlcBusyException)
        {
            // Checked before its base type PlcUnavailableException.
            return (CommandOutcome.Failed, CommandReasons.PlcBusy);
        }
        catch (PlcUnavailableException)
        {
            return (CommandOutcome.Failed, CommandReasons.PlcUnavailable);
        }
        catch (PlcAccessDeniedException)
        {
            return (CommandOutcome.Failed, CommandReasons.PlcAccessDenied);
        }
        catch (Exception ex)
        {
            // PlcWriteOutcomeUnknownException and anything unexpected: the value may have been applied.
            logger.LogError(ex, "Write to {TagId} ended with an unknown outcome.", tag.TagId);
            return (CommandOutcome.Unknown, CommandReasons.WriteOutcomeUnknown);
        }
    }

    private async Task<CommandDisposition> StoredAsync(
        DeviceCommand command, long expiresMs, CommandOutcome outcome, string? reason, CancellationToken ct)
    {
        var id = command.CommandId.ToString("D");
        var resultJson = Result(command, outcome, reason);
        Log(command, outcome, reason);

        if (await TryCompleteAsync(id, resultJson, expiresMs, ct).ConfigureAwait(false))
        {
            return new CommandDisposition(id, resultJson, Stored: true, Acknowledge: true);
        }

        if (outcome == CommandOutcome.Unknown)
        {
            // Row 6: a "started" row exists, so even after a restart this command can only become "unknown"
            // again; publishing now is safe, and the in-memory copy keeps the payload identical.
            _unstored[id] = (resultJson, expiresMs);
            return new CommandDisposition(id, resultJson, Stored: false, Acknowledge: false);
        }

        // Rows 8–10 (user decision): an answer that is not on disk is not published. Otherwise a restart would
        // forget it and a redelivery — fresh by then — could write the PLC after "rejected" was reported.
        // Unacknowledged, the message comes back and is decided again once storage works.
        return new CommandDisposition(id, null, Stored: false, Acknowledge: false);
    }

    private CommandDisposition NotStored(DeviceCommand command, CommandOutcome outcome, string reason)
    {
        Log(command, outcome, reason);
        return new CommandDisposition(command.CommandId.ToString("D"), Result(command, outcome, reason), Stored: false, Acknowledge: true);
    }

    private async Task<bool> TryCompleteAsync(string id, string resultJson, long expiresMs, CancellationToken ct)
    {
        try
        {
            await log.CompleteAsync(id, resultJson, expiresMs, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure here (SQLite error, cancellation at shutdown, disposed connection) leaves the
            // result in memory; the caller decides what that means.
            logger.LogError(ex, "Could not store the result of command {CommandId}.", id);
            return false;
        }
    }

    private string Result(DeviceCommand command, CommandOutcome outcome, string? reason) => ContractJson.Serialize(new CommandResult
    {
        SchemaVersion = ContractJson.SchemaVersion,
        CommandId = command.CommandId,
        EdgeId = _edgeId,
        Outcome = outcome,
        Reason = reason,
        CompletedAtUtc = clock.GetUtcNow(),
    });

    // Never the target value or issuedBy.
    private void Log(DeviceCommand command, CommandOutcome outcome, string? reason) =>
        logger.LogInformation("Command {CommandId} on {TagId}: {Outcome} {Reason}", command.CommandId, command.TagId, outcome, reason);

    private void Log(DeviceCommand command, string what) =>
        logger.LogInformation("Command {CommandId} on {TagId}: {What}", command.CommandId, command.TagId, what);
}
