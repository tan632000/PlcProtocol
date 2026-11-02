using System.Buffers;
using System.Text;
using System.Text.Json;
using IndustrialIot.Cloud.Api;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Live;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace IndustrialIot.Cloud.Ingestion;

/// <summary>One stored PLC: its status entry and its tags in the Edge's order.</summary>
public sealed record InventoryDocumentDevice(InventoryDevice Status, IReadOnlyList<InventoryTag> Tags);

/// <summary>The stored inventory of one Edge (the <c>edge_inventory.document</c> JSON, written with <see cref="ContractJson.Options"/>).</summary>
public sealed record InventoryDocument(bool CommandsEnabled, IReadOnlyList<InventoryDocumentDevice> Devices);

/// <summary>
/// Keeps the latest complete inventory round of each Edge from <c>iiot/{edgeId}/inventory</c>, subscribed at QoS 0 so
/// an offline Cloud never has the broker queue them. The MQTT handler only assembles parts, in one bounded buffer per
/// Edge, and turns a complete, consistent round into that Edge's waiting round; it never touches the database, so a
/// database outage cannot hold up the message pipeline. A background loop stores waiting rounds (at most once per 5 s
/// per Edge, as soon as that gap allows) unless the stored one is a newer round of the same boot, and retries a failed
/// store; <c>received_utc</c> is when the round completed. The stored document is re-serialized from the parsed parts,
/// never the raw payload.
/// </summary>
internal sealed class InventoryIngestor(
    CloudMqttConnection connection,
    IServiceScopeFactory scopes,
    LiveNotifier live,
    IOptions<EdgesOptions> edges,
    IOptions<CommandOptions> commands,
    TimeProvider clock,
    ILogger<InventoryIngestor> logger) : BackgroundService
{
    public const string TopicFilter = "iiot/+/inventory";

    /// <summary>The most one open round may hold: 200 parts of 12 KiB.</summary>
    internal const long MaxBufferBytes = 2400 * 1024;

    private const int MaxPayloadBytes = 16 * 1024;
    private static readonly TimeSpan BufferLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StoreGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SupersededMemory = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, EdgeState> _edges = new(StringComparer.Ordinal);

    // Completed when a round starts waiting, so the loop stores it without waiting for its next tick.
    private TaskCompletionSource _roundCompleted = NewSignal();

    // Registering here (before ApplicationStarted) means the first MQTT connection already subscribes.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        connection.RegisterSubscription(TopicFilter, HandleAsync, MqttQualityOfServiceLevel.AtMostOnce);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Renewed before flushing, so a round completing during the flush wakes the next wait.
                var signal = NewSignal();
                Volatile.Write(ref _roundCompleted, signal);
                try
                {
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Not a database outage (DatabaseRetry handles those): the rounds stay waiting and the next pass tries again.
                    logger.LogWarning("Inventory store pass failed ({Reason}).", ex.GetBaseException().Message);
                }

                try
                {
                    await signal.Task.WaitAsync(FlushInterval, clock, stoppingToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The 1 s tick: a waiting round may now be past its 5 s gap.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    /// <summary>The open buffer of <paramref name="edgeId"/>: parts and payload bytes held; null when none.</summary>
    internal (int Parts, long Bytes)? BufferOf(string edgeId)
    {
        lock (_gate)
        {
            return _edges.TryGetValue(edgeId, out var state) && state.Buffer is { } buffer ? (buffer.Parts.Count, buffer.Bytes) : null;
        }
    }

    /// <summary>Whether <paramref name="edgeId"/> has a complete round the loop has not stored yet.</summary>
    internal bool IsWaiting(string edgeId)
    {
        lock (_gate)
        {
            return _edges.TryGetValue(edgeId, out var state) && state.Waiting is not null;
        }
    }

    internal Task HandleAsync(MqttApplicationMessage message, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var part = Validate(message, out var edgeId, out var size);
        if (edgeId is null || part is null)
        {
            return Task.CompletedTask;
        }

        bool completed;
        lock (_gate)
        {
            if (!_edges.TryGetValue(edgeId, out var state))
            {
                state = new EdgeState();
                _edges[edgeId] = state;
            }

            completed = Accept(state, part, size, now, message.Topic);
        }

        if (completed)
        {
            Volatile.Read(ref _roundCompleted).TrySetResult();
        }

        return Task.CompletedTask;
    }

    /// <summary>The part, or null after a warning; <paramref name="edgeId"/> is null when the topic is not an allowed Edge's.</summary>
    private EdgeInventory? Validate(MqttApplicationMessage message, out string? edgeId, out int size)
    {
        size = 0;
        edgeId = MqttTopicFilter.EdgeIdOf(message.Topic, "inventory");
        if (edgeId is null || !edges.Value.IsAllowed(edgeId))
        {
            edgeId = null;
            Drop(message.Topic, "unknown edge");
            return null;
        }

        var payload = message.Payload;
        if (payload.Length > MaxPayloadBytes)
        {
            Drop(message.Topic, "payload larger than 16 KiB");
            return null;
        }

        size = (int)payload.Length;
        EdgeInventory part;
        try
        {
            part = ContractJson.Deserialize<EdgeInventory>(Encoding.UTF8.GetString(payload.ToArray()));
        }
        catch (ContractValidationException)
        {
            // No detail: the exception text (even a JSON path) can echo payload content.
            Drop(message.Topic, "invalid payload");
            return null;
        }

        // The topic is what the broker ACL binds to the Edge's credentials; the payload is just text.
        if (!string.Equals(part.EdgeId, edgeId, StringComparison.Ordinal))
        {
            Drop(message.Topic, "payload edgeId does not match the topic");
            return null;
        }

        return part;
    }

    /// <summary>
    /// Adds <paramref name="part"/> to the Edge's buffer; a complete, consistent round becomes the waiting one. True when
    /// a round started waiting.
    /// </summary>
    private bool Accept(EdgeState state, EdgeInventory part, int size, DateTimeOffset now, string topic)
    {
        foreach (var expired in state.Superseded.Where(s => s.Value <= now).Select(s => s.Key).ToList())
        {
            state.Superseded.Remove(expired);
        }

        if (state.Superseded.ContainsKey(part.BootId))
        {
            Drop(topic, "two Edge processes share edgeId (a replaced boot is still sending)");
            return false;
        }

        if (state.Buffer is { } open && now - open.Started >= BufferLifetime)
        {
            state.Buffer = null;
        }

        var buffer = state.Buffer;
        if (buffer is not null && (buffer.BootId != part.BootId || buffer.Round != part.Round))
        {
            if (buffer.BootId == part.BootId && part.Round < buffer.Round)
            {
                Drop(topic, "an older round of this boot");
                return false;
            }

            buffer = null; // one open round per Edge: a newer round (or another boot) replaces it
        }

        if (buffer is null)
        {
            buffer = new RoundBuffer(part.BootId, part.Round, part.PartCount, now);
            state.Buffer = buffer;
        }
        else if (buffer.PartCount != part.PartCount)
        {
            Drop(topic, "partCount differs from the open round");
            return false;
        }

        if (buffer.Parts.ContainsKey(part.Part))
        {
            return false; // a redelivered part
        }

        if (buffer.Bytes + size > MaxBufferBytes)
        {
            Drop(topic, "the open round is over 2400 KiB");
            return false;
        }

        buffer.Parts[part.Part] = part;
        buffer.Bytes += size;
        if (buffer.Parts.Count < buffer.PartCount)
        {
            return false;
        }

        state.Buffer = null;
        if (Assemble(buffer) is not { } document)
        {
            Drop(topic, $"inconsistent round: {buffer.Problem}");
            return false;
        }

        // A late round never replaces a newer one of the same boot that is still waiting.
        if (state.Waiting is { } waiting && waiting.BootId == buffer.BootId && waiting.Round >= buffer.Round)
        {
            Drop(topic, "an older round of this boot");
            return false;
        }

        // Milliseconds, the contract's precision: the database keeps it exactly, so a push and a later GET agree.
        var completed = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), now.Offset);
        state.Waiting = new WaitingRound(buffer.BootId, buffer.Round, document, completed);
        return true;
    }

    /// <summary>The round as one document, or null with <see cref="RoundBuffer.Problem"/> set to a fixed reason.</summary>
    private static string? Assemble(RoundBuffer buffer)
    {
        var parts = buffer.Parts.OrderBy(p => p.Key).Select(p => p.Value).ToList();
        if (parts.Select(p => p.CommandsEnabled).Distinct().Count() > 1)
        {
            buffer.Problem = "commandsEnabled differs";
            return null;
        }

        var statuses = parts.Where(p => p.Devices is not null).SelectMany(p => p.Devices!).ToList();
        if (statuses.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != statuses.Count)
        {
            buffer.Problem = "device names repeat";
            return null;
        }

        var tags = statuses.ToDictionary(d => d.Name, _ => new List<InventoryTag>(), StringComparer.Ordinal);
        foreach (var part in parts.Where(p => p.Tags is not null))
        {
            if (!tags.TryGetValue(part.Tags!.Device, out var list))
            {
                buffer.Problem = "tags for an unknown device";
                return null;
            }

            list.AddRange(part.Tags.Items);
        }

        if (statuses.Any(d => tags[d.Name].Count != d.TagCount))
        {
            buffer.Problem = "tag count differs";
            return null;
        }

        var all = tags.Values.SelectMany(t => t).ToList();
        if (all.Select(t => t.TagId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Count)
        {
            buffer.Problem = "tagId repeats";
            return null;
        }

        var document = new InventoryDocument(parts[0].CommandsEnabled, [.. statuses.Select(d => new InventoryDocumentDevice(d, tags[d.Name]))]);
        return JsonSerializer.Serialize(document, ContractJson.Options);
    }

    /// <summary>Stores every waiting round whose Edge is past its 5 s gap.</summary>
    private async Task FlushAsync(CancellationToken ct)
    {
        List<(string EdgeId, EdgeState State, WaitingRound Round)> due;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            due = [.. _edges
                .Where(e => e.Value.Waiting is not null && now >= e.Value.NextStore)
                .Select(e => (e.Key, e.Value, e.Value.Waiting!))];
        }

        foreach (var (edgeId, state, waiting) in due)
        {
            await TryStoreAsync(edgeId, state, waiting, ct).ConfigureAwait(false);
        }
    }

    private async Task TryStoreAsync(string edgeId, EdgeState state, WaitingRound waiting, CancellationToken ct)
    {
        Guid? replaced = null;
        var stored = false;
        var ok = await DatabaseRetry.RunAsync(
            async token => (stored, replaced) = await StoreAsync(edgeId, waiting, token).ConfigureAwait(false),
            clock,
            logger,
            $"iiot/{edgeId}/inventory",
            ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (!ok)
            {
                // Kept: the loop tries again, unless a newer round has replaced it by then.
                logger.LogWarning("The inventory round of edge {EdgeId} is kept until the database is back.", edgeId);
                return;
            }

            if (ReferenceEquals(state.Waiting, waiting))
            {
                state.Waiting = null;
            }

            if (stored)
            {
                state.NextStore = clock.GetUtcNow() + StoreGap;
                if (replaced is { } previous)
                {
                    state.Superseded[previous] = clock.GetUtcNow() + SupersededMemory;
                }
            }
        }

        if (stored)
        {
            // After the commit, with the body GET .../devices now answers. Never throws into the store loop.
            DevicesResponse body;
            try
            {
                var row = new EdgeInventoryRow
                {
                    EdgeId = edgeId,
                    BootId = waiting.BootId,
                    Round = waiting.Round,
                    ReceivedUtc = waiting.CompletedUtc,
                    Document = waiting.Document,
                };
                body = DevicesEndpoints.ToResponse(edgeId, row, clock.GetUtcNow(), commands.Value.Enabled);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning("The devicesUpdated push for edge {EdgeId} could not be built ({Reason}).", edgeId, ex.GetBaseException().Message);
                return;
            }

            await live.DevicesUpdatedAsync(body).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces the Edge's row unless it holds a round of the same boot at least as new. Returns whether it was
    /// stored and, when the replaced row was another boot's, that boot.
    /// </summary>
    private async Task<(bool Stored, Guid? Replaced)> StoreAsync(string edgeId, WaitingRound round, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var row = await db.EdgeInventory.SingleOrDefaultAsync(r => r.EdgeId == edgeId, ct).ConfigureAwait(false);
        Guid? replaced = null;
        if (row is null)
        {
            db.EdgeInventory.Add(new EdgeInventoryRow
            {
                EdgeId = edgeId,
                BootId = round.BootId,
                Round = round.Round,
                ReceivedUtc = round.CompletedUtc,
                Document = round.Document,
            });
        }
        else if (row.BootId == round.BootId && row.Round >= round.Round)
        {
            return (false, null);
        }
        else
        {
            replaced = row.BootId == round.BootId ? null : row.BootId;
            row.BootId = round.BootId;
            row.Round = round.Round;
            row.ReceivedUtc = round.CompletedUtc;
            row.Document = round.Document;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (true, replaced);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Drop(string topic, string reason) =>
        logger.LogWarning("Dropping inventory on '{Topic}': {Reason}.", topic, reason);

    private sealed class EdgeState
    {
        public RoundBuffer? Buffer { get; set; }

        public WaitingRound? Waiting { get; set; }

        public DateTimeOffset NextStore { get; set; } = DateTimeOffset.MinValue;

        /// <summary>Boots whose stored round another boot replaced, until when their parts are refused.</summary>
        public Dictionary<Guid, DateTimeOffset> Superseded { get; } = [];
    }

    private sealed class RoundBuffer(Guid bootId, long round, int partCount, DateTimeOffset started)
    {
        public Guid BootId { get; } = bootId;

        public long Round { get; } = round;

        public int PartCount { get; } = partCount;

        public DateTimeOffset Started { get; } = started;

        public Dictionary<int, EdgeInventory> Parts { get; } = [];

        public long Bytes { get; set; }

        public string? Problem { get; set; }
    }

    /// <summary>A complete round to store; <see cref="CompletedUtc"/> (its last part's arrival) becomes <c>received_utc</c>.</summary>
    private sealed record WaitingRound(Guid BootId, long Round, string Document, DateTimeOffset CompletedUtc);
}
