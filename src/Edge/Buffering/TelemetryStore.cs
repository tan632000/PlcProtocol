using System.Globalization;
using System.Text.Json;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Buffering;

/// <summary>One polled value before it gets its stream identity and sequence.</summary>
public sealed record TelemetrySample(string TagId, JsonElement? Value, TelemetryQuality Quality, DateTimeOffset TimestampUtc);

/// <summary>A queued sample: its sequence and the exact JSON that will be published.</summary>
public sealed record StoredTelemetry(long Sequence, string Payload);

/// <summary>
/// Durable telemetry queue (store-and-forward). Samples get their <c>streamId</c> and consecutive
/// <c>sequence</c> in the same transaction that stores them, so a crash can never hand out a number
/// twice or skip one. Rows leave the queue only when acknowledged (published) or purged by the limits.
/// </summary>
public sealed class TelemetryStore
{
    private const int PurgeChunkSize = 5_000;

    private readonly EdgeDatabase _database;
    private readonly string _edgeId;
    private readonly BufferOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TelemetryStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TaskCompletionSource _appended = NewSignal();

    public TelemetryStore(
        EdgeDatabase database,
        IOptions<EdgeOptions> edgeOptions,
        IOptions<BufferOptions> bufferOptions,
        TimeProvider clock,
        ILogger<TelemetryStore> logger)
    {
        _database = database;
        _edgeId = edgeOptions.Value.EdgeId;
        _options = bufferOptions.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Stores a whole poll cycle in one transaction. If any sample is invalid nothing is stored and
    /// <c>next_sequence</c> is unchanged.
    /// </summary>
    public async Task<IReadOnlyList<TelemetryPayload>> AppendBatchAsync(IReadOnlyList<TelemetrySample> samples, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return [];
        }

        IReadOnlyList<TelemetryPayload> payloads;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false))
            await using (var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                var streamId = Guid.Parse(await ReadMetaAsync(connection, tx, "stream_id", ct).ConfigureAwait(false));
                var sequence = long.Parse(await ReadMetaAsync(connection, tx, "next_sequence", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                // Never older than the watermark: a clock reset to the past must not make fresh samples
                // look old enough for the next age purge.
                var createdMs = Math.Max(_clock.GetUtcNow().ToUnixTimeMilliseconds(), _database.LastSeenUtcMs);

                var built = new List<TelemetryPayload>(samples.Count);
                await using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO telemetry (sequence, created_utc_ms, payload) VALUES ($sequence, $created, $payload);";
                var sequenceParameter = insert.Parameters.Add("$sequence", SqliteType.Integer);
                insert.Parameters.AddWithValue("$created", createdMs);
                var payloadParameter = insert.Parameters.Add("$payload", SqliteType.Text);

                foreach (var sample in samples)
                {
                    var payload = new TelemetryPayload
                    {
                        SchemaVersion = ContractJson.SchemaVersion,
                        EdgeId = _edgeId,
                        StreamId = streamId,
                        Sequence = sequence,
                        TagId = sample.TagId,
                        Value = sample.Value,
                        Quality = sample.Quality,
                        TimestampUtc = sample.TimestampUtc,
                    };

                    sequenceParameter.Value = sequence;
                    payloadParameter.Value = ContractJson.Serialize(payload); // validates; throws before commit
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    built.Add(payload);
                    sequence++;
                }

                await WriteMetaAsync(connection, tx, "next_sequence", sequence.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                payloads = built;
            }

            // The batch is stored from here on: wake the publisher, and never report failure to the caller,
            // which would otherwise retry and store the samples twice.
            Interlocked.Exchange(ref _appended, NewSignal()).TrySetResult();
            await TrimAfterAppendAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        return payloads;
    }

    private async Task TrimAfterAppendAsync()
    {
        try
        {
            if (await CountAsync(CancellationToken.None).ConfigureAwait(false) > _options.MaxRows)
            {
                await PurgeCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (SqliteException ex)
        {
            // The hourly purge will catch up.
            _logger.LogError(ex, "Telemetry buffer trim after append failed; will retry on the next purge.");
        }
    }

    /// <summary>Completes when the next batch is committed, or after <paramref name="timeout"/>.</summary>
    public async Task WaitForAppendAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await Volatile.Read(ref _appended).Task.WaitAsync(timeout, _clock, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Nothing new; the caller simply checks the queue again.
        }
    }

    /// <summary>Oldest queued samples first.</summary>
    public async Task<IReadOnlyList<StoredTelemetry>> ReadBatchAsync(int max, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);

        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence, payload FROM telemetry ORDER BY sequence LIMIT $max;";
        command.Parameters.AddWithValue("$max", max);

        var rows = new List<StoredTelemetry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new StoredTelemetry(reader.GetInt64(0), reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>Removes a published sample. An unknown sequence is ignored.</summary>
    public async Task AcknowledgeAsync(long sequence, CancellationToken ct)
    {
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM telemetry WHERE sequence = $sequence;";
        command.Parameters.AddWithValue("$sequence", sequence);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Queued rows, as <c>next_sequence − oldest sequence</c>: exact unless acknowledgements left holes,
    /// and never a full table scan.
    /// </summary>
    public async Task<long> CountAsync(CancellationToken ct)
    {
        // One statement, so next_sequence and the oldest row come from the same snapshot.
        await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CAST((SELECT value FROM meta WHERE key = 'next_sequence') AS INTEGER), (SELECT MIN(sequence) FROM telemetry);";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return reader.IsDBNull(1) ? 0 : reader.GetInt64(0) - reader.GetInt64(1);
    }

    /// <summary>Drops samples beyond the age and row limits, oldest first. Returns the number dropped.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PurgeCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<int> PurgeCoreAsync(CancellationToken ct)
    {
        // Persist the watermark first: if the clock later jumps back, the rollback is still detectable.
        await _database.PersistWatermarkAsync(ct).ConfigureAwait(false);
        if (_database.IsClockRolledBack)
        {
            return 0;
        }

        var cutoffMs = _clock.GetUtcNow().AddHours(-_options.MaxAgeHours).ToUnixTimeMilliseconds();
        var dropped = await DeleteInChunksAsync(AgeCondition, AgeOrder, cutoffMs, "older than MaxAgeHours", ct).ConfigureAwait(false);

        await using (var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            var next = long.Parse(await ReadMetaAsync(connection, null, "next_sequence", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            dropped += await DeleteInChunksAsync("sequence < $limit", "sequence", next - _options.MaxRows, "beyond MaxRows", ct).ConfigureAwait(false);
        }

        return dropped;
    }

    // Ordering by the indexed column lets SQLite seek telemetry_created instead of scanning every row
    // (ORDER BY sequence would make it walk the table in rowid order to avoid a sort).
    internal const string AgeCondition = "created_utc_ms < $limit";
    internal const string AgeOrder = "created_utc_ms, sequence";

    internal static string ChunkRangeSql(string condition, string orderBy) =>
        $"SELECT MIN(sequence), MAX(sequence) FROM (SELECT sequence FROM telemetry WHERE {condition} ORDER BY {orderBy} LIMIT {PurgeChunkSize});";

    // condition/orderBy are internal constants only; the limit is always a bound parameter.
    private async Task<int> DeleteInChunksAsync(string condition, string orderBy, long limit, string why, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using var range = connection.CreateCommand();
            range.Transaction = tx;
            range.CommandText = ChunkRangeSql(condition, orderBy);
            range.Parameters.AddWithValue("$limit", limit);

            long first, last;
            await using (var reader = await range.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
                {
                    return total;
                }

                first = reader.GetInt64(0);
                last = reader.GetInt64(1);
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = $"DELETE FROM telemetry WHERE sequence BETWEEN $first AND $last AND {condition};";
            delete.Parameters.AddWithValue("$first", first);
            delete.Parameters.AddWithValue("$last", last);
            delete.Parameters.AddWithValue("$limit", limit);
            var count = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            total += count;
            _logger.LogWarning(
                "Telemetry buffer dropped {Count} samples {Why} (sequence {First}..{Last}).", count, why, first, last);
        }
    }

    private static async Task<string> ReadMetaAsync(SqliteConnection connection, SqliteTransaction? tx, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Buffer database is missing meta key '{key}'.");
    }

    private static async Task WriteMetaAsync(SqliteConnection connection, SqliteTransaction tx, string key, string value, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE meta SET value = $value WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
