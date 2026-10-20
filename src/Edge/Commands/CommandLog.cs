using IndustrialIot.Edge.Buffering;
using Microsoft.Data.Sqlite;

namespace IndustrialIot.Edge.Commands;

/// <summary>One command id's durable record.</summary>
internal sealed record CommandLogEntry(string CommandId, bool Completed, string? Result, bool ResultSent);

/// <summary>
/// Durable memory of processed commands, in the Edge database file. A <c>started</c> row is committed
/// before the PLC write, so a crash mid-write is detected on redelivery instead of writing twice.
/// Rows are never removed while their command could still be fresh.
/// </summary>
internal sealed class CommandLog(EdgeDatabase database, TimeProvider clock, ILogger<CommandLog> logger)
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS command_log (
            command_id TEXT PRIMARY KEY,
            state TEXT NOT NULL,
            result TEXT NULL,
            result_sent INTEGER NOT NULL DEFAULT 0,
            expires_utc_ms INTEGER NOT NULL,
            created_utc_ms INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS command_log_unsent ON command_log (created_utc_ms) WHERE result_sent = 0;
        """;

    private readonly SemaphoreSlim _createLock = new(1, 1);
    private volatile bool _created;

    /// <summary>Upper bound on rows; see <see cref="HasCapacityAsync"/>.</summary>
    internal int MaxRows { get; set; } = 100_000;

    public async Task<CommandLogEntry?> FindAsync(string commandId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, result, result_sent FROM command_log WHERE command_id = $id;";
        command.Parameters.AddWithValue("$id", commandId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new CommandLogEntry(
            commandId,
            reader.GetString(0) == "completed",
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2) == 1);
    }

    /// <summary>Records that the PLC write is about to start. Throws if it cannot be committed.</summary>
    public async Task InsertStartedAsync(string commandId, long expiresUtcMs, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO command_log (command_id, state, result, result_sent, expires_utc_ms, created_utc_ms)
            VALUES ($id, 'started', NULL, 0, $expires, $created);
            """;
        command.Parameters.AddWithValue("$id", commandId);
        command.Parameters.AddWithValue("$expires", expiresUtcMs);
        command.Parameters.AddWithValue("$created", clock.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stores the final result (inserting the row if there was no <c>started</c> row), unsent.</summary>
    public async Task CompleteAsync(string commandId, string resultJson, long expiresUtcMs, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO command_log (command_id, state, result, result_sent, expires_utc_ms, created_utc_ms)
            VALUES ($id, 'completed', $result, 0, $expires, $created)
            ON CONFLICT (command_id) DO UPDATE SET state = 'completed', result = $result, result_sent = 0
                WHERE command_log.state = 'started'; -- a stored result is never replaced
            """;
        command.Parameters.AddWithValue("$id", commandId);
        command.Parameters.AddWithValue("$result", resultJson);
        command.Parameters.AddWithValue("$expires", expiresUtcMs);
        command.Parameters.AddWithValue("$created", clock.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task MarkSentAsync(string commandId, CancellationToken ct) => SetSentAsync(commandId, sent: true, ct);

    /// <summary>A re-published result counts as unsent until its PUBACK.</summary>
    public Task MarkUnsentAsync(string commandId, CancellationToken ct) => SetSentAsync(commandId, sent: false, ct);

    /// <summary>Completed results not yet acknowledged by the broker, oldest first.</summary>
    public async Task<IReadOnlyList<(string CommandId, string Result)>> UnsentAsync(int max, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT command_id, result FROM command_log
            WHERE result_sent = 0 AND state = 'completed'
            ORDER BY created_utc_ms LIMIT $max;
            """;
        command.Parameters.AddWithValue("$max", max);
        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>
    /// True when another row fits. When full, first removes sent rows whose commands can no longer be
    /// fresh (never during a clock rollback); fresh rows are never evicted.
    /// </summary>
    public async Task<bool> HasCapacityAsync(CancellationToken ct)
    {
        if (await CountAsync(ct).ConfigureAwait(false) < MaxRows)
        {
            return true;
        }

        await PurgeAsync(ct).ConfigureAwait(false);
        return await CountAsync(ct).ConfigureAwait(false) < MaxRows;
    }

    /// <summary>Deletes sent rows past expiry + skew. Persists the clock watermark first; skipped during a rollback.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        await database.PersistWatermarkAsync(ct).ConfigureAwait(false);
        if (database.IsClockRolledBack)
        {
            return 0;
        }

        var cutoff = clock.GetUtcNow().ToUnixTimeMilliseconds() - (long)EdgeDatabase.RollbackTolerance.TotalMilliseconds;
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM command_log WHERE result_sent = 1 AND expires_utc_ms < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        var deleted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
        {
            logger.LogInformation("Command log purge removed {Count} expired entries.", deleted);
        }

        return deleted;
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM command_log;";
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private async Task SetSentAsync(string commandId, bool sent, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE command_log SET result_sent = $sent WHERE command_id = $id;";
        command.Parameters.AddWithValue("$sent", sent ? 1 : 0);
        command.Parameters.AddWithValue("$id", commandId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (!_created)
        {
            await _createLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_created)
                {
                    await database.EnsureTableAsync(CreateSql, ct).ConfigureAwait(false);
                    _created = true;
                }
            }
            finally
            {
                _createLock.Release();
            }
        }

        return await database.OpenConnectionAsync(ct).ConfigureAwait(false);
    }
}
