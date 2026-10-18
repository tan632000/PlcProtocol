using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Buffering;

/// <summary>
/// Owns the Edge's single SQLite file: every connection comes from here with the same durability
/// settings, the schema is checked once at host start, and the clock watermark lives here because
/// both the telemetry buffer and the command log must refuse to purge after a clock rollback.
/// </summary>
public sealed class EdgeDatabase
{
    /// <summary>A clock this far behind the highest time ever seen is treated as rolled back.</summary>
    public static readonly TimeSpan RollbackTolerance = TimeSpan.FromMinutes(2);

    private const int SchemaVersion = 1;
    private static readonly TimeSpan WatermarkPersistInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly ILogger<EdgeDatabase> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _watermarkLock = new(1, 1);

    private long _lastSeenUtcMs;
    private long _lastPersistedUtcMs;
    private int _rollbackWarned;

    public EdgeDatabase(IOptions<BufferOptions> options, IHostEnvironment environment, TimeProvider clock, ILogger<EdgeDatabase> logger)
    {
        _clock = clock;
        _logger = logger;
        FilePath = System.IO.Path.GetFullPath(options.Value.Path, environment.ContentRootPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public string FilePath { get; }

    /// <summary>True while the clock is more than <see cref="RollbackTolerance"/> behind the highest time seen.</summary>
    public bool IsClockRolledBack
    {
        get
        {
            var rolledBack = _clock.GetUtcNow().ToUnixTimeMilliseconds() < Volatile.Read(ref _lastSeenUtcMs) - (long)RollbackTolerance.TotalMilliseconds;
            if (!rolledBack)
            {
                Interlocked.Exchange(ref _rollbackWarned, 0);
            }
            else if (Interlocked.Exchange(ref _rollbackWarned, 1) == 0)
            {
                _logger.LogWarning(
                    "System clock is behind the highest time seen before; purges are paused and new commands are rejected until it is corrected (check NTP/RTC).");
            }

            return rolledBack;
        }
    }

    /// <summary>Opens a new connection with the Edge's durability settings applied.</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // synchronous=FULL: a committed "started" command row must survive a power cut before the PLC write.
            await ExecuteAsync(connection, "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;", ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Called once at host start. Fails the start instead of silently replacing a bad file.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            // Permissions are fixed before SQLite writes anything: it creates -wal/-shm with the
            // database file's mode at that moment.
            PrepareOwnerOnlyFiles();

            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Buffer database '{FilePath}' has schema version {version}; this Edge supports up to {SchemaVersion}.");
            }

            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                """
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS telemetry (
                    sequence INTEGER PRIMARY KEY,
                    created_utc_ms INTEGER NOT NULL,
                    payload TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS telemetry_created ON telemetry (created_utc_ms);
                """,
                ct).ConfigureAwait(false);

            var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            await using (var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                await InsertMetaIfMissingAsync(connection, tx, "stream_id", Guid.NewGuid().ToString("D"), ct).ConfigureAwait(false);
                await InsertMetaIfMissingAsync(connection, tx, "next_sequence", "0", ct).ConfigureAwait(false);
                await InsertMetaIfMissingAsync(connection, tx, "last_seen_utc_ms", nowMs.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", ct, tx).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            var stored = Convert.ToInt64(
                await ScalarAsync(connection, "SELECT value FROM meta WHERE key = 'last_seen_utc_ms';", ct).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            Volatile.Write(ref _lastSeenUtcMs, stored);
            _lastPersistedUtcMs = stored;
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException($"Buffer database '{FilePath}' cannot be opened: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Buffer database '{FilePath}' cannot be created: {ex.Message}", ex);
        }
    }

    /// <summary>Creates an additional table (e.g. the command log) in the same file.</summary>
    public async Task EnsureTableAsync(string createSql, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(connection, createSql, ct).ConfigureAwait(false);
    }

    /// <summary>Raises the watermark to "now" and persists it if a minute has passed since the last write.</summary>
    public Task ObserveClockAsync(CancellationToken ct) => UpdateWatermarkAsync(force: false, ct);

    /// <summary>Raises and persists the watermark now. Every purge calls this first.</summary>
    public Task PersistWatermarkAsync(CancellationToken ct) => UpdateWatermarkAsync(force: true, ct);

    private async Task UpdateWatermarkAsync(bool force, CancellationToken ct)
    {
        await _watermarkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            var lastSeen = Math.Max(Volatile.Read(ref _lastSeenUtcMs), nowMs);
            Volatile.Write(ref _lastSeenUtcMs, lastSeen);

            var due = lastSeen - _lastPersistedUtcMs >= (long)WatermarkPersistInterval.TotalMilliseconds;
            if (lastSeen == _lastPersistedUtcMs || (!force && !due))
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = $value WHERE key = 'last_seen_utc_ms';";
            command.Parameters.AddWithValue("$value", lastSeen.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _lastPersistedUtcMs = lastSeen;
        }
        finally
        {
            _watermarkLock.Release();
        }
    }

    /// <summary>The highest UTC time seen (ms); never moves backwards.</summary>
    internal long LastSeenUtcMs => Volatile.Read(ref _lastSeenUtcMs);

    // Telemetry and the command log are only for the Edge service account: directory 0700, files 0600.
    private void PrepareOwnerOnlyFiles()
    {
        var directory = System.IO.Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        const UnixFileMode ownerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode ownerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory, ownerDirectory);
        }
        else if ((new DirectoryInfo(directory).UnixFileMode & ~ownerDirectory) != 0)
        {
            // Never chmod a directory the Edge did not create (it may be /tmp or /var/lib, shared with
            // other services); the database files themselves are still made owner-only below.
            _logger.LogWarning(
                "Buffer directory '{Directory}' is accessible to other users; use a private directory for Edge:Buffer:Path.", directory);
        }

        if (!File.Exists(FilePath))
        {
            // An empty file is a valid new SQLite database; creating it here fixes its mode up front.
            using var _ = new FileStream(FilePath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = ownerFile,
            });
        }
        else
        {
            Tighten(FilePath, ownerFile);
        }

        foreach (var sidecar in new[] { FilePath + "-wal", FilePath + "-shm" })
        {
            if (File.Exists(sidecar))
            {
                Tighten(sidecar, ownerFile);
            }
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private void Tighten(string path, UnixFileMode wanted)
    {
        var current = File.GetUnixFileMode(path);
        if ((current & ~wanted) != 0)
        {
            File.SetUnixFileMode(path, current & wanted);
            _logger.LogWarning("Permissions of '{Path}' were broader than owner-only and have been tightened.", path);
        }
    }

    private static async Task InsertMetaIfMissingAsync(SqliteConnection connection, SqliteTransaction tx, string key, string value, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT OR IGNORE INTO meta (key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct, SqliteTransaction? tx = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Opens the database before any other Edge service starts (registered first) and keeps the clock
/// watermark fresh while the host runs.
/// </summary>
internal sealed class EdgeDatabaseStarter(EdgeDatabase database, TimeProvider clock, ILogger<EdgeDatabaseStarter> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await database.PersistWatermarkAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or OperationCanceledException or InvalidOperationException)
        {
            // Shutdown must not fail because of this; the watermark from the last minute is still on disk.
            logger.LogWarning(ex, "Could not persist the clock watermark at shutdown.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(false);
                try
                {
                    await database.ObserveClockAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (SqliteException ex)
                {
                    // An exception escaping a BackgroundService stops the host; a busy file is not worth that.
                    logger.LogError(ex, "Could not persist the clock watermark; will retry.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }
}
