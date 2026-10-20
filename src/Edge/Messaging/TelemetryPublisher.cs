using System.Text;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace IndustrialIot.Edge.Messaging;

/// <summary>
/// Drains the telemetry queue to the broker in <c>sequence</c> order and deletes a row only when the Cloud
/// confirms it with a <see cref="TelemetryAck"/> — a broker PUBACK alone proves nothing about the Cloud, whose
/// broker queue may overflow while it is offline. At most <see cref="MqttOptions.MaxUnacknowledged"/> sent
/// rows wait for confirmation. Progress means the oldest queued row was confirmed: confirmations for later rows
/// alone must not hide one sample whose ack was lost. Without progress for the ack timeout (doubling up to
/// <see cref="MaxAckTimeout"/>), or after a reconnect, sending restarts from the oldest queued row. Resends
/// are duplicates the Cloud absorbs, so no sample is lost while it stays in the buffer.
/// </summary>
internal sealed class TelemetryPublisher(
    MqttConnection connection,
    MqttTopics topics,
    TelemetryStore store,
    IOptions<MqttOptions> options,
    IOptions<EdgeOptions> edge,
    TimeProvider clock,
    ILogger<TelemetryPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxAckTimeout = TimeSpan.FromSeconds(600);

    /// <summary>Cursor value meaning "before the oldest queued row": every row is sent (again).</summary>
    private const long BeforeOldest = -1;

    private readonly int _batchSize = options.Value.PublishBatchSize;
    private readonly int _window = options.Value.MaxUnacknowledged;
    private readonly TimeSpan _ackTimeoutStart = TimeSpan.FromSeconds(options.Value.AckTimeoutSeconds);
    private readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(options.Value.ReconnectMinDelayMs);
    private readonly Lock _progressLock = new();

    // Publish loop only.
    private long _cursor = BeforeOldest;
    private long _generation;

    // Shared with the ack handler (under _progressLock).
    private TimeSpan _ackTimeout;
    private DateTimeOffset _lastProgress;
    private long _progressHead = long.MaxValue; // oldest queued sequence when the timer (re)started
    private TaskCompletionSource _removed = NewSignal();

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Before the host finishes starting, so the first connection already subscribes.
        connection.RegisterSubscription(topics.TelemetryAcks, HandleAckAsync);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_progressLock)
        {
            _ackTimeout = _ackTimeoutStart;
            _lastProgress = clock.GetUtcNow();
        }

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

    /// <summary>Publishes what the window allows. Returns false when something failed and the loop should back off.</summary>
    private async Task<bool> PublishBatchAsync(CancellationToken ct)
    {
        IReadOnlyList<StoredTelemetry> batch;
        long unconfirmed;
        try
        {
            // A new broker session may have lost anything the old one had not delivered yet.
            var generation = connection.ConnectionGeneration;
            if (generation != _generation)
            {
                _generation = generation;
                await ResendFromOldestAsync("reconnected", doubleTimeout: false, ct).ConfigureAwait(false);
            }

            unconfirmed = _cursor == BeforeOldest ? 0 : await store.CountUpToAsync(_cursor, ct).ConfigureAwait(false);
            if (unconfirmed > 0 && AckTimedOut())
            {
                await ResendFromOldestAsync("no confirmation from the Cloud", doubleTimeout: true, ct).ConfigureAwait(false);
                unconfirmed = 0;
            }

            var room = _window - unconfirmed;
            if (room <= 0)
            {
                await WaitForProgressOrAppendAsync(ct).ConfigureAwait(false);
                return true;
            }

            batch = await store.ReadBatchAfterAsync(_cursor, (int)Math.Min(_batchSize, room), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reading the telemetry buffer failed.");
            return false;
        }

        if (batch.Count == 0)
        {
            await WaitForProgressOrAppendAsync(ct).ConfigureAwait(false);
            return true;
        }

        if (unconfirmed == 0)
        {
            // Nothing was outstanding: the ack timeout starts with this send, and this row is the head to watch.
            RestartAckTimer(batch[0].Sequence);
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

            // Sent, not confirmed: the row stays until the Cloud's ack removes it.
            _cursor = row.Sequence;
        }

        return true;
    }

    private async Task ResendFromOldestAsync(string reason, bool doubleTimeout, CancellationToken ct)
    {
        // Before any I/O: a failing read below must not leave the old cursor in place, or the resend is lost.
        var hadSent = _cursor != BeforeOldest;
        _cursor = BeforeOldest;
        TimeSpan nextTimeout;
        lock (_progressLock)
        {
            if (doubleTimeout)
            {
                _ackTimeout = TimeSpan.FromTicks(Math.Min(_ackTimeout.Ticks * 2, MaxAckTimeout.Ticks));
            }

            _lastProgress = clock.GetUtcNow();
            _progressHead = long.MaxValue; // set again by the first send after the reset
            nextTimeout = _ackTimeout;
        }

        if (!hadSent)
        {
            return;
        }

        try
        {
            logger.LogWarning("Sending queued telemetry again from the oldest sample ({Reason}); {Queued} queued, next confirmation timeout {Timeout} s.",
                reason, await store.CountAsync(ct).ConfigureAwait(false), (int)nextTimeout.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Sending queued telemetry again from the oldest sample ({Reason}); counting the queue failed ({Error}).",
                reason, ex.GetBaseException().Message);
        }
    }

    private bool AckTimedOut()
    {
        lock (_progressLock)
        {
            return clock.GetUtcNow() - _lastProgress >= _ackTimeout;
        }
    }

    private void RestartAckTimer(long head)
    {
        lock (_progressLock)
        {
            _lastProgress = clock.GetUtcNow();
            _progressHead = head;
        }
    }

    /// <summary>Returns when a confirmation removed rows, on the next appended batch, or after <see cref="IdleWait"/>.</summary>
    private async Task WaitForProgressOrAppendAsync(CancellationToken ct)
    {
        Task removed;
        lock (_progressLock)
        {
            removed = _removed.Task;
        }

        await Task.WhenAny(store.WaitForAppendAsync(IdleWait, ct), removed).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Never throws: a failed delete is logged and the MQTT message still acknowledged, because an unacknowledged
    /// ack would occupy the session's in-flight slots until the next reconnect. Acks are idempotent and the
    /// resend after the timeout recovers anything a failed delete left behind.
    /// </summary>
    private async Task HandleAckAsync(MqttApplicationMessage message, CancellationToken ct)
    {
        TelemetryAck ack;
        try
        {
            ack = ContractJson.Deserialize<TelemetryAck>(Encoding.UTF8.GetString(message.Payload));
        }
        catch (ContractValidationException)
        {
            logger.LogWarning("Ignoring an invalid telemetry ack on '{Topic}'.", message.Topic);
            return;
        }

        if (!string.Equals(ack.EdgeId, edge.Value.EdgeId, StringComparison.Ordinal))
        {
            logger.LogWarning("Ignoring a telemetry ack addressed to another edge on '{Topic}'.", message.Topic);
            return;
        }

        try
        {
            if (await store.AcknowledgeRangeAsync(ack.StreamId, ack.FromSequence, ack.ToSequence, ct).ConfigureAwait(false) == 0)
            {
                return; // a duplicate or foreign-stream ack: nothing changed
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Removing confirmed telemetry {From}..{To} failed; those samples will be sent again.",
                ack.FromSequence, ack.ToSequence);
            return;
        }

        long? oldest;
        bool headKnown;
        try
        {
            oldest = await store.OldestSequenceAsync(ct).ConfigureAwait(false);
            headKnown = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The rows are gone; only the progress check is skipped (at worst one extra resend after the timeout).
            logger.LogWarning("Confirmed telemetry {From}..{To} removed, but reading the oldest queued sample failed ({Error}).",
                ack.FromSequence, ack.ToSequence, ex.GetBaseException().Message);
            oldest = null;
            headKnown = false;
        }

        TaskCompletionSource removed;
        lock (_progressLock)
        {
            // Progress only when the head moved: acks for later rows must not keep an unconfirmed head waiting.
            if (headKnown && (oldest is null || oldest > _progressHead))
            {
                _ackTimeout = _ackTimeoutStart;
                _lastProgress = clock.GetUtcNow();
                _progressHead = oldest ?? long.MaxValue;
            }

            removed = _removed;
            _removed = NewSignal();
        }

        removed.TrySetResult(); // the window may have room again
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

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
