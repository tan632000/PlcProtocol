using System.Buffers;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Live;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MQTTnet;
using Npgsql;
using NpgsqlTypes;

namespace IndustrialIot.Cloud.Ingestion;

/// <summary>
/// Stores telemetry from <c>iiot/{edgeId}/telemetry</c>: once per (edge, stream, sequence), newest value per
/// tag, acknowledged only after the transaction committed. Invalid or spoofed messages are dropped and
/// acknowledged, so they are not redelivered forever.
/// <para>
/// After a sample is stored (or found already stored), or fails a contract rule that can never pass later,
/// the owning Edge gets a <see cref="TelemetryAck"/> on <c>iiot/{edgeId}/telemetry-acks</c>: the only signal
/// that lets it delete the sample. Drops that may become valid later (unsupported schema version, a timestamp
/// ahead of this clock) and drops that do not provably belong to the topic's Edge are never acked.
/// </para>
/// </summary>
internal sealed class TelemetryIngestor(
    CloudMqttConnection connection,
    IServiceScopeFactory scopes,
    LiveNotifier live,
    IOptions<EdgesOptions> edges,
    TimeProvider clock,
    ILogger<TelemetryIngestor> logger) : IHostedService
{
    public const string TopicFilter = "iiot/+/telemetry";
    private const int MaxPayloadBytes = 16 * 1024;
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);
    private static readonly JsonDocumentOptions RejectionParseOptions = new() { AllowDuplicateProperties = false };

    // Registering here (before ApplicationStarted) means the first MQTT connection already subscribes.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        connection.RegisterSubscription(TopicFilter, HandleAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task HandleAsync(MqttApplicationMessage message, CancellationToken ct)
    {
        var receivedUtc = clock.GetUtcNow();
        var (sample, rejectionAck) = Validate(message, receivedUtc);
        if (sample is null)
        {
            if (rejectionAck is not null)
            {
                await PublishAckAsync(rejectionAck, ct).ConfigureAwait(false);
            }

            return; // dropped: acknowledged
        }

        var inserted = false;
        var stored = await DatabaseRetry.RunAsync(async token => inserted = await StoreAsync(sample, receivedUtc, token).ConfigureAwait(false), clock, logger, message.Topic, ct)
            .ConfigureAwait(false);
        if (stored && inserted)
        {
            // After the commit, and only for a new row: a redelivered duplicate is not pushed again.
            await live.TelemetryStoredAsync(sample).ConfigureAwait(false);
        }

        if (!stored)
        {
            // Retrying in place did not help; a new session makes the broker deliver it again.
            await connection.ForceReconnectAsync().ConfigureAwait(false);
            throw new MessageNotAcknowledgedException("the database stayed unavailable; reconnecting for redelivery.");
        }

        // Committed (inserted now or by an earlier delivery): the Edge may delete it.
        await PublishAckAsync(AckFor(sample.EdgeId, sample.StreamId, sample.Sequence), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Best effort: an ack that is not confirmed only delays the delete, because the Edge resends unacked samples
    /// and the primary key absorbs the duplicate.
    /// </summary>
    private async Task PublishAckAsync(TelemetryAck ack, CancellationToken ct)
    {
        var outcome = await connection.PublishAsync($"iiot/{ack.EdgeId}/telemetry-acks", ContractJson.Serialize(ack), ct).ConfigureAwait(false);
        if (outcome != PublishOutcome.Confirmed)
        {
            logger.LogWarning("Telemetry ack for edge {EdgeId}, stream {StreamId}, sequence {Sequence} was not confirmed ({Outcome}); the Edge will resend the sample.",
                ack.EdgeId, ack.StreamId, ack.FromSequence, outcome);
        }
    }

    private static TelemetryAck AckFor(string edgeId, Guid streamId, long sequence) => new()
    {
        SchemaVersion = ContractJson.SchemaVersion,
        EdgeId = edgeId,
        StreamId = streamId,
        FromSequence = sequence,
        ToSequence = sequence,
    };

    /// <summary>
    /// The sample to store; or, after a warning naming topic and reason only, no sample and either the ack for a
    /// permanently invalid sample or null when the drop must not be acked.
    /// </summary>
    private (TelemetryPayload? Sample, TelemetryAck? RejectionAck) Validate(MqttApplicationMessage message, DateTimeOffset receivedUtc)
    {
        var topicEdgeId = MqttTopicFilter.EdgeIdOf(message.Topic, "telemetry");
        if (topicEdgeId is null || !edges.Value.IsAllowed(topicEdgeId))
        {
            return Drop(message.Topic, "unknown edge");
        }

        var payload = message.Payload;
        if (payload.Length > MaxPayloadBytes)
        {
            return Drop(message.Topic, "payload larger than 16 KiB");
        }

        var json = Encoding.UTF8.GetString(payload.ToArray());
        TelemetryPayload sample;
        try
        {
            sample = ContractJson.Deserialize<TelemetryPayload>(json);
        }
        catch (ContractValidationException)
        {
            // No detail: the exception text (even a JSON path) can echo payload content.
            Drop(message.Topic, "invalid payload");
            return (null, RejectionAck(json, topicEdgeId));
        }

        // The topic is what the broker ACL binds to the Edge's credentials; the payload is just text.
        if (!string.Equals(sample.EdgeId, topicEdgeId, StringComparison.Ordinal))
        {
            return Drop(message.Topic, "payload edgeId does not match the topic");
        }

        if (sample.TimestampUtc > receivedUtc + MaxClockSkew)
        {
            return Drop(message.Topic, "timestamp more than 2 minutes in the future");
        }

        return (sample, null);
    }

    private (TelemetryPayload?, TelemetryAck?) Drop(string topic, string reason)
    {
        logger.LogWarning("Dropping telemetry on '{Topic}': {Reason}.", topic, reason);
        return (null, null);
    }

    /// <summary>
    /// The ack for a payload that broke a contract rule yet still names this topic's Edge, a stream and a sequence
    /// under the current schema version: no later delivery of those bytes can pass, so the Edge may drop them.
    /// Anything less certain (other version, other Edge, no stream or sequence) is not acked and stays on the Edge.
    /// </summary>
    private static TelemetryAck? RejectionAck(string json, string topicEdgeId)
    {
        try
        {
            // The same decoded text and the same duplicate rule as ContractJson: two parsers must never disagree
            // on which sequence a payload names, or the ack could delete a different, undelivered sample.
            using var document = JsonDocument.Parse(json, RejectionParseOptions);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var schemaVersion) && schemaVersion == ContractJson.SchemaVersion
                && root.TryGetProperty("edgeId", out var edgeId) && edgeId.ValueKind == JsonValueKind.String
                && string.Equals(edgeId.GetString(), topicEdgeId, StringComparison.Ordinal)
                && root.TryGetProperty("streamId", out var stream) && stream.ValueKind == JsonValueKind.String
                && Guid.TryParse(stream.GetString(), out var streamId) && streamId != Guid.Empty
                && root.TryGetProperty("sequence", out var sequence) && sequence.ValueKind == JsonValueKind.Number
                && sequence.TryGetInt64(out var sequenceNumber) && sequenceNumber >= 0)
            {
                return AckFor(topicEdgeId, streamId, sequenceNumber);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Not even JSON, a duplicated property, or a string that cannot be decoded (e.g. a lone surrogate escape
            // such as "\uD800"): nothing identifies the sample. Never let it escape: an exception here would leave the
            // message unacknowledged and redelivered forever, blocking every Edge behind it.
        }

        return null;
    }

    /// <summary>True when the sample was new (inserted), false for a duplicate.</summary>
    private async Task<bool> StoreAsync(TelemetryPayload sample, DateTimeOffset receivedUtc, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var value = CanonicalJson(sample.Value);
        var quality = JsonNamingPolicy.CamelCase.ConvertName(sample.Quality.ToString());

        // The primary key is the dedupe key: a redelivered sample inserts nothing.
        var inserted = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO telemetry (edge_id, stream_id, sequence, tag_id, value, quality, timestamp_utc, received_utc)
            VALUES (@edge, @stream, @sequence, @tag, @value, @quality, @timestamp, @received)
            ON CONFLICT (edge_id, stream_id, sequence) DO NOTHING
            """,
            [
                new NpgsqlParameter("edge", sample.EdgeId),
                new NpgsqlParameter("stream", sample.StreamId),
                new NpgsqlParameter("sequence", sample.Sequence),
                new NpgsqlParameter("tag", sample.TagId),
                Json("value", value),
                new NpgsqlParameter("quality", quality),
                new NpgsqlParameter("timestamp", sample.TimestampUtc),
                new NpgsqlParameter("received", receivedUtc),
            ],
            ct).ConfigureAwait(false);

        if (inserted == 1)
        {
            // Samples can arrive out of order (store-and-forward replay): only a newer one replaces the latest value.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO latest_values (edge_id, tag_id, value, quality, timestamp_utc, stream_id, sequence)
                VALUES (@edge, @tag, @value, @quality, @timestamp, @stream, @sequence)
                ON CONFLICT (edge_id, tag_id) DO UPDATE
                SET value = EXCLUDED.value, quality = EXCLUDED.quality, timestamp_utc = EXCLUDED.timestamp_utc,
                    stream_id = EXCLUDED.stream_id, sequence = EXCLUDED.sequence
                WHERE latest_values.timestamp_utc < EXCLUDED.timestamp_utc
                """,
                [
                    new NpgsqlParameter("edge", sample.EdgeId),
                    new NpgsqlParameter("tag", sample.TagId),
                    Json("value", value),
                    new NpgsqlParameter("quality", quality),
                    new NpgsqlParameter("timestamp", sample.TimestampUtc),
                    new NpgsqlParameter("stream", sample.StreamId),
                    new NpgsqlParameter("sequence", sample.Sequence),
                ],
                ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return inserted == 1;
    }

    /// <summary>
    /// The value the contract accepted (a bool or finite double), re-written as JSON. The raw text could be
    /// something like <c>1e-20000</c>: valid as a double, but beyond what PostgreSQL's numeric can store.
    /// </summary>
    private static string? CanonicalJson(JsonElement? value) => value switch
    {
        null => null,
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { } number => JsonSerializer.Serialize(number.GetDouble()),
    };

    private static NpgsqlParameter Json(string name, string? json) =>
        new(name, NpgsqlDbType.Jsonb) { Value = (object?)json ?? DBNull.Value };
}

/// <summary>
/// Retries one database write in place: back-off 0.5 s doubling to 5 s, for at most 30 s, all on the
/// injected clock. Messages are processed one at a time, so later ones wait meanwhile.
/// </summary>
internal static class DatabaseRetry
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>True once <paramref name="attempt"/> succeeded; false when it still failed after the budget.</summary>
    public static async Task<bool> RunAsync(
        Func<CancellationToken, Task> attempt,
        TimeProvider clock,
        ILogger logger,
        string topic,
        CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var delay = FirstDelay;
        while (true)
        {
            try
            {
                await attempt(ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsDatabaseFailure(ex))
            {
                if (clock.GetUtcNow() - started >= Budget)
                {
                    logger.LogError("Storing a message from '{Topic}' kept failing for {Seconds} s ({Reason}).",
                        topic, (int)Budget.TotalSeconds, ex.GetBaseException().Message);
                    return false;
                }

                logger.LogWarning("Storing a message from '{Topic}' failed ({Reason}); retrying in {Delay} ms.",
                    topic, ex.GetBaseException().Message, (int)delay.TotalMilliseconds);
            }

            await Task.Delay(delay, clock, ct).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
        }
    }

    // Other exceptions are bugs, not outages: they escape, so the message stays unacknowledged without a retry loop.
    private static bool IsDatabaseFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is DbException or DbUpdateException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}
