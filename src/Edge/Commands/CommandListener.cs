using System.Buffers;
using IndustrialIot.Edge.Messaging;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace IndustrialIot.Edge.Commands;

/// <summary>
/// Connects <see cref="CommandExecutor"/> to <c>iiot/{edgeId}/commands</c> and publishes results to
/// <c>iiot/{edgeId}/command-results</c>. Results that could not be published (or stored) are sent again
/// while connected; every re-send carries the identical payload, so the Cloud can deduplicate.
/// <para>
/// With <see cref="CommandsOptions.Enabled"/> off (the default) it does nothing at all: no subscription, no replay,
/// no purge, so no command can reach the executor.
/// </para>
/// </summary>
internal sealed class CommandListener(
    MqttConnection connection,
    MqttTopics topics,
    CommandExecutor executor,
    CommandLog log,
    IOptions<CommandsOptions> options,
    TimeProvider clock,
    ILogger<CommandListener> logger) : BackgroundService
{
    private static readonly TimeSpan ReplayInterval = TimeSpan.FromSeconds(1);

    // Results stuck in memory mean storage is failing; re-sending them every second would only spam the Cloud.
    private static readonly TimeSpan UnstoredReplayInterval = TimeSpan.FromSeconds(30);
    private DateTimeOffset _nextUnstoredReplay = DateTimeOffset.MinValue;
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private const int ReplayBatch = 100;

    private readonly bool _enabled = options.Value.Enabled;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            logger.LogInformation("Remote writes are disabled ({Key}=false); the Edge does not subscribe to commands.",
                CommandsOptions.SectionName + ":Enabled");

            // A write-mode run may have left the subscription and queued commands in the persistent session.
            connection.RetireSubscription(topics.Commands);
            return Task.CompletedTask;
        }

        // Registered before the connection opens, so a command queued in the persistent session is never
        // delivered without a handler.
        connection.RegisterSubscription(topics.Commands, HandleAsync);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurge = clock.GetUtcNow() + PurgeInterval;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(ReplayInterval, clock, stoppingToken).ConfigureAwait(false);
                try
                {
                    if (connection.IsConnected)
                    {
                        await ReplayAsync(stoppingToken).ConfigureAwait(false);
                    }

                    if (clock.GetUtcNow() >= nextPurge)
                    {
                        nextPurge = clock.GetUtcNow() + PurgeInterval;
                        await log.PurgeAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // An exception escaping a BackgroundService stops the host; retry on the next tick.
                    logger.LogError(ex, "Command result replay or log purge failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    /// <summary>
    /// Handles one delivery. Returning acknowledges it; throwing leaves it unacknowledged so the broker
    /// delivers it again (used when the outcome is not durably recorded yet).
    /// </summary>
    private async Task HandleAsync(MqttApplicationMessage message, CancellationToken ct)
    {
        var disposition = await executor.ExecuteAsync(message.Payload.ToArray(), ct).ConfigureAwait(false);

        if (disposition.ResultJson is { } result)
        {
            await PublishAsync(disposition.CommandId!, result, disposition.Stored, ct).ConfigureAwait(false);
        }

        if (!disposition.Acknowledge)
        {
            throw new InvalidOperationException(
                $"The outcome of command {disposition.CommandId} is not stored yet; leaving the message unacknowledged.");
        }
    }

    private async Task ReplayAsync(CancellationToken ct)
    {
        // Results that never reached the database first, then stored but unsent ones (oldest first).
        foreach (var id in await executor.StoreUnstoredAsync(ct).ConfigureAwait(false))
        {
            logger.LogInformation("Stored the result of command {CommandId} after an earlier failure.", id);
        }

        if (executor.UnstoredResults.Count > 0 && clock.GetUtcNow() >= _nextUnstoredReplay)
        {
            _nextUnstoredReplay = clock.GetUtcNow() + UnstoredReplayInterval;
            foreach (var (id, (result, _)) in executor.UnstoredResults)
            {
                await PublishAsync(id, result, stored: false, ct).ConfigureAwait(false);
            }
        }

        foreach (var (id, result) in await log.UnsentAsync(ReplayBatch, ct).ConfigureAwait(false))
        {
            if (!await PublishAsync(id, result, stored: true, ct).ConfigureAwait(false))
            {
                return; // not connected any more; try again later
            }
        }
    }

    private async Task<bool> PublishAsync(string commandId, string resultJson, bool stored, CancellationToken ct)
    {
        try
        {
            await connection.PublishAsync(topics.CommandResults, resultJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Publishing the result of command {CommandId} failed ({Reason}); it will be sent again.",
                commandId, ex.GetBaseException().Message);
            return false;
        }

        if (stored)
        {
            await log.MarkSentAsync(commandId, ct).ConfigureAwait(false);
        }

        return true;
    }
}
