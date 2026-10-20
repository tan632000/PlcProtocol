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
        if (Validate(message, receivedUtc) is not { } sample)
        {
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
    }

    /// <summary>The sample to store, or null (after a warning naming topic and reason only) when it must be dropped.</summary>
    private TelemetryPayload? Validate(MqttApplicationMessage message, DateTimeOffset receivedUtc)
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

        TelemetryPayload sample;
        try
        {
            sample = ContractJson.Deserialize<TelemetryPayload>(Encoding.UTF8.GetString(payload.ToArray()));
        }
        catch (ContractValidationException)
        {
            // No detail: the exception text (even a JSON path) can echo payload content.
            return Drop(message.Topic, "invalid payload");
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

        return sample;
    }

    private TelemetryPayload? Drop(string topic, string reason)
    {
        logger.LogWarning("Dropping telemetry on '{Topic}': {Reason}.", topic, reason);
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
