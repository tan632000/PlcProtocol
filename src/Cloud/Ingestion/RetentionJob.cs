using IndustrialIot.Cloud.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IndustrialIot.Cloud.Ingestion;

/// <summary>
/// Every hour (injected clock) deletes telemetry the Cloud received more than <c>Cloud:Retention:TelemetryDays</c>
/// ago, 10,000 rows per transaction. <c>latest_values</c> is never touched.
/// </summary>
internal sealed class RetentionJob(
    IServiceScopeFactory scopes,
    IOptions<RetentionOptions> options,
    TimeProvider clock,
    ILogger<RetentionJob> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private const int BatchSize = 10_000;

    private long _waitsArmed;

    /// <summary>How many times the job has started waiting for its next run (test hook).</summary>
    internal long WaitsArmed => Interlocked.Read(ref _waitsArmed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Create the timer before reporting "waiting": a test may move the clock as soon as it sees it.
                var wait = Task.Delay(Interval, clock, stoppingToken);
                Interlocked.Increment(ref _waitsArmed);
                await wait.ConfigureAwait(false);

                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // The next run catches up; a failed run deletes nothing it did not commit.
                    logger.LogWarning("Telemetry retention run failed ({Reason}).", ex.GetBaseException().Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    /// <summary>Deletes every expired row, one batch per transaction. Returns how many rows were deleted.</summary>
    internal async Task<long> RunOnceAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(options.Value.TelemetryDays);
        long total = 0;
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();

            // The sub-select walks ix_telemetry_received; each statement is its own transaction.
            var deleted = await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM telemetry
                WHERE (edge_id, stream_id, sequence) IN (
                    SELECT edge_id, stream_id, sequence FROM telemetry
                    WHERE received_utc < @cutoff
                    LIMIT @batch)
                """,
                [new NpgsqlParameter("cutoff", cutoff), new NpgsqlParameter("batch", BatchSize)],
                ct).ConfigureAwait(false);

            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            logger.LogInformation("Telemetry retention deleted {Rows} rows received before {Cutoff:O}.", total, cutoff);
        }

        return total;
    }
}
