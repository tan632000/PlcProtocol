using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Health;

/// <summary>Refreshes <see cref="ReadinessState"/> every <c>Cloud:Health:RefreshSeconds</c> on the injected clock.</summary>
internal sealed class ReadinessMonitor(
    IServiceScopeFactory scopes,
    ReadinessState state,
    CloudMqttConnection mqtt,
    IOptions<HealthOptions> options,
    TimeProvider clock,
    ILogger<ReadinessMonitor> logger) : BackgroundService
{
    public const string Mqtt = "mqtt";

    private readonly TimeSpan _interval = TimeSpan.FromSeconds(options.Value.RefreshSeconds);
    private long _waitsArmed;

    /// <summary>How many times the monitor has started waiting for its next refresh (test hook).</summary>
    internal long WaitsArmed => Interlocked.Read(ref _waitsArmed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The first refresh would otherwise race the first MQTT connect and report "not ready" for a whole period.
            await mqtt.FirstAttemptCompleted.WaitAsync(stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                state.Set(ReadinessState.Database, await DatabaseIsReachableAsync(stoppingToken).ConfigureAwait(false));
                state.Set(Mqtt, mqtt.IsConnected);

                // Create the timer before reporting "waiting": a test may move the clock as soon as it sees it.
                var wait = Task.Delay(_interval, clock, stoppingToken);
                Interlocked.Increment(ref _waitsArmed);
                await wait.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    private async Task<bool> DatabaseIsReachableAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Readiness: database check failed ({Reason}).", ex.GetBaseException().Message);
            return false;
        }
    }
}
