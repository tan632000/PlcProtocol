using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Cloud.Commands;

/// <summary>
/// Every 10 s (injected clock) marks commands still waiting for their Edge result <c>timed_out</c> once
/// <c>expiresAtUtc + TimeoutGraceSeconds</c> has passed — including rows left <c>pending</c> by a crash between
/// storing and publishing. A result that arrives later still replaces <c>timed_out</c>.
/// </summary>
internal sealed class CommandTimeoutSweeper(
    IServiceScopeFactory scopes,
    IOptions<CommandOptions> options,
    LiveNotifier live,
    TimeProvider clock,
    ILogger<CommandTimeoutSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private long _waitsArmed;

    /// <summary>How many times the sweeper has started waiting for its next pass (test hook).</summary>
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
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // The next pass catches up.
                    logger.LogWarning("Command timeout sweep failed ({Reason}).", ex.GetBaseException().Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromSeconds(options.Value.TimeoutGraceSeconds);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();

        var due = await db.Commands.AsNoTracking()
            .Where(c => CommandStatus.AwaitingResult.Contains(c.Status) && c.ExpiresAtUtc < cutoff)
            .Select(c => c.CommandId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (due.Count == 0)
        {
            return;
        }

        // Guarded by status in the same statement, so a result stored since the select is never overwritten.
        var timedOut = await db.Commands
            .Where(c => due.Contains(c.CommandId) && CommandStatus.AwaitingResult.Contains(c.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatus.TimedOut), ct)
            .ConfigureAwait(false);
        if (timedOut == 0)
        {
            return; // every due command got its Edge result first
        }

        logger.LogWarning("{Count} command(s) timed out without an Edge result.", timedOut);

        // Each push re-reads its row, so a command whose result won the race is pushed with that result.
        foreach (var commandId in due)
        {
            await live.CommandChangedAsync(commandId).ConfigureAwait(false);
        }
    }
}
