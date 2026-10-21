using System.Buffers;
using System.Text;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Ingestion;
using IndustrialIot.Cloud.Live;
using IndustrialIot.Cloud.Messaging;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace IndustrialIot.Cloud.Commands;

/// <summary>
/// Records Edge results from <c>iiot/{edgeId}/command-results</c>. The first outcome wins and is final; it is
/// accepted in every non-final status, including <c>pending</c> (the result can beat the PUBACK) and
/// <c>timed_out</c> (a late answer is still the truth about the PLC).
/// </summary>
internal sealed class CommandResultIngestor(
    CloudMqttConnection connection,
    IServiceScopeFactory scopes,
    LiveNotifier live,
    IOptions<EdgesOptions> edges,
    IOptions<CommandOptions> options,
    TimeProvider clock,
    ILogger<CommandResultIngestor> logger) : IHostedService
{
    public const string TopicFilter = "iiot/+/command-results";
    private const int MaxPayloadBytes = 16 * 1024;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // With commands off nothing can have been sent, so no result is consumed (and no row ever changes).
        if (options.Value.Enabled)
        {
            connection.RegisterSubscription(TopicFilter, HandleAsync);
        }
        else
        {
            // A write-mode run may have left the subscription and queued results in the persistent session.
            connection.RetireSubscription(TopicFilter);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task HandleAsync(MqttApplicationMessage message, CancellationToken ct)
    {
        if (Validate(message) is not { } result)
        {
            return; // dropped: acknowledged
        }

        var receivedUtc = clock.GetUtcNow();
        var changed = false;
        var stored = await DatabaseRetry.RunAsync(async token => changed = await StoreAsync(result, receivedUtc, message.Topic, token).ConfigureAwait(false), clock, logger, message.Topic, ct)
            .ConfigureAwait(false);
        if (stored && changed)
        {
            // After the update committed, outside the retry: a push problem never causes a redelivery.
            await live.CommandChangedAsync(result.CommandId).ConfigureAwait(false);
        }

        if (!stored)
        {
            await connection.ForceReconnectAsync().ConfigureAwait(false);
            throw new MessageNotAcknowledgedException("the database stayed unavailable; reconnecting for redelivery.");
        }
    }

    private CommandResult? Validate(MqttApplicationMessage message)
    {
        var topicEdgeId = MqttTopicFilter.EdgeIdOf(message.Topic, "command-results");
        if (topicEdgeId is null || !edges.Value.IsAllowed(topicEdgeId))
        {
            return Drop(message.Topic, "unknown edge");
        }

        if (message.Payload.Length > MaxPayloadBytes)
        {
            return Drop(message.Topic, "payload larger than 16 KiB");
        }

        CommandResult result;
        try
        {
            result = ContractJson.Deserialize<CommandResult>(Encoding.UTF8.GetString(message.Payload.ToArray()));
        }
        catch (ContractValidationException)
        {
            return Drop(message.Topic, "invalid payload");
        }

        // The topic is bound to the Edge's broker credentials; the payload is not.
        return string.Equals(result.EdgeId, topicEdgeId, StringComparison.Ordinal)
            ? result
            : Drop(message.Topic, "payload edgeId does not match the topic");
    }

    private CommandResult? Drop(string topic, string reason)
    {
        logger.LogWarning("Dropping a command result on '{Topic}': {Reason}.", topic, reason);
        return null;
    }

    /// <summary>True when this result became the command's outcome.</summary>
    private async Task<bool> StoreAsync(CommandResult result, DateTimeOffset receivedUtc, string topic, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var status = CommandStatus.FromOutcome(result.Outcome);

        // One guarded statement: only a command of this Edge that has no Edge outcome yet takes it.
        var updated = await db.Commands
            .Where(c => c.CommandId == result.CommandId
                        && c.EdgeId == result.EdgeId
                        && CommandStatus.NonFinal.Contains(c.Status))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.Status, status)
                    .SetProperty(c => c.Reason, result.Reason)
                    .SetProperty(c => c.CompletedAtUtc, result.CompletedAtUtc)
                    .SetProperty(c => c.ResultReceivedUtc, receivedUtc),
                ct)
            .ConfigureAwait(false);

        if (updated == 1)
        {
            logger.LogInformation("Command {CommandId} on {EdgeId}: Edge outcome {Status}.", result.CommandId, result.EdgeId, status);
            return true;
        }

        var current = await db.Commands.AsNoTracking()
            .Where(c => c.CommandId == result.CommandId && c.EdgeId == result.EdgeId)
            .Select(c => c.Status)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (current is null)
        {
            // Unknown here, or a command of another Edge: either way not this Edge's to answer.
            Drop(topic, "no such command for this edge");
        }
        else if (current != status)
        {
            logger.LogWarning("Command {CommandId} already ended as {Current}; ignoring a later {Status} result.", result.CommandId, current, status);
        }

        return false;
    }
}
