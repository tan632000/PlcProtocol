using IndustrialIot.Edge.Buffering;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Messaging;

/// <summary>
/// Drains the telemetry queue to the broker strictly in <c>sequence</c> order. A row is deleted only
/// after its PUBACK; if a publish fails, the batch stops and the same row goes first next time, so
/// samples are never skipped or reordered (a crash after PUBACK may resend one; the Cloud dedupes).
/// </summary>
internal sealed class TelemetryPublisher(
    MqttConnection connection,
    MqttTopics topics,
    TelemetryStore store,
    IOptions<MqttOptions> options,
    TimeProvider clock,
    ILogger<TelemetryPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly int _batchSize = options.Value.PublishBatchSize;
    private readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(options.Value.ReconnectMinDelayMs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurge = clock.GetUtcNow(); // first purge right away: a device that restarts often still purges
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (clock.GetUtcNow() >= nextPurge)
                {
                    await PurgeAsync(stoppingToken).ConfigureAwait(false);
                    nextPurge = clock.GetUtcNow() + PurgeInterval;
                }

                // Wait for the broker only until the next purge is due, so a long outage cannot postpone it.
                var untilPurge = nextPurge - clock.GetUtcNow();
                try
                {
                    await connection.WaitForConnectionAsync(stoppingToken)
                        .WaitAsync(untilPurge > TimeSpan.Zero ? untilPurge : TimeSpan.Zero, clock, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (!await PublishBatchAsync(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(_retryDelay, clock, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    /// <summary>Publishes one batch. Returns false when something failed and the loop should back off.</summary>
    private async Task<bool> PublishBatchAsync(CancellationToken ct)
    {
        IReadOnlyList<StoredTelemetry> batch;
        try
        {
            batch = await store.ReadBatchAsync(_batchSize, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reading the telemetry buffer failed.");
            return false;
        }

        if (batch.Count == 0)
        {
            await store.WaitForAppendAsync(IdleWait, ct).ConfigureAwait(false);
            return true;
        }

        foreach (var row in batch)
        {
            try
            {
                await connection.PublishAsync(topics.Telemetry, row.Payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Publishing telemetry sequence {Sequence} failed ({Reason}); it stays queued.",
                    row.Sequence, ex.GetBaseException().Message);
                return false;
            }

            try
            {
                await store.AcknowledgeAsync(row.Sequence, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Already delivered; it will be sent again and deduplicated by the Cloud.
                logger.LogError(ex, "Removing published telemetry sequence {Sequence} failed.", row.Sequence);
                return false;
            }
        }

        return true;
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            await store.PurgeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Hourly telemetry buffer purge failed.");
        }
    }
}
