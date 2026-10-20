using System.Text.Json;
using IndustrialIot.Cloud.Api;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IndustrialIot.Cloud.Live;

/// <summary>
/// Pushes stored data to the Edge's group. Called only after the data is committed, never throws, and gives
/// up on a push after <see cref="PushTimeout"/>: a slow or unreachable client must not stall ingestion or commands.
/// </summary>
public sealed class LiveNotifier(IHubContext<LiveHub> hub, IServiceScopeFactory scopes, ILogger<LiveNotifier> logger) : IDisposable
{
    public const string TelemetryMethod = "telemetry";
    public const string CommandUpdatedMethod = "commandUpdated";

    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(2);

    // Command pushes run one at a time and each re-reads the row: a push sent later can never carry an older
    // status than one sent before it, whichever writer (API, result ingestor, sweeper) triggered it.
    private readonly SemaphoreSlim _commandPushes = new(1, 1);

    /// <summary>The sample in its wire form (the Shared contract JSON), not SignalR's own serialization.</summary>
    public async Task TelemetryStoredAsync(TelemetryPayload sample)
    {
        try
        {
            using var wire = JsonDocument.Parse(ContractJson.Serialize(sample));
            await SendAsync(sample.EdgeId, TelemetryMethod, wire.RootElement.Clone()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log(TelemetryMethod, sample.EdgeId, ex);
        }
    }

    /// <summary>Pushes the command's current stored state (the <c>GET /api/v1/commands/{commandId}</c> shape).</summary>
    public async Task CommandChangedAsync(Guid commandId)
    {
        await _commandPushes.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(PushTimeout);
            await using var scope = scopes.CreateAsyncScope();
            var command = await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Commands.AsNoTracking()
                .SingleOrDefaultAsync(c => c.CommandId == commandId, timeout.Token)
                .ConfigureAwait(false);
            if (command is not null)
            {
                await SendAsync(command.EdgeId, CommandUpdatedMethod, Detail(command), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log(CommandUpdatedMethod, commandId.ToString(), ex);
        }
        finally
        {
            _commandPushes.Release();
        }
    }

    public void Dispose() => _commandPushes.Dispose();

    private async Task SendAsync(string edgeId, string method, object payload, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PushTimeout);
        await hub.Clients.Group(LiveHub.GroupOf(edgeId)).SendAsync(method, payload, timeout.Token).ConfigureAwait(false);
    }

    // Same mapping as CommandEndpoints' detail response.
    private static CommandDetail Detail(CommandRow command)
    {
        using var value = JsonDocument.Parse(command.TargetValue);
        return new CommandDetail(
            command.CommandId,
            command.EdgeId,
            command.TagId,
            value.RootElement.Clone(),
            command.Status,
            command.Reason,
            command.IssuedBy,
            command.IssuedAtUtc.UtcDateTime,
            command.ExpiresAtUtc.UtcDateTime,
            command.CompletedAtUtc?.UtcDateTime);
    }

    private void Log(string method, string subject, Exception ex) =>
        logger.LogWarning("Live push '{Method}' for {Subject} failed ({Reason}).", method, subject, ex.GetBaseException().Message);
}
