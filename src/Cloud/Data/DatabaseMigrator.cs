using Microsoft.EntityFrameworkCore;

namespace IndustrialIot.Cloud.Data;

/// <summary>
/// Applies pending migrations before anything else starts (registered as the first hosted service).
/// A failure stops the host instead of serving with an outdated or missing schema.
/// </summary>
internal sealed class DatabaseMigrator(IServiceScopeFactory scopes, ILogger<DatabaseMigrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        try
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Database schema is up to date.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The provider message names the failure (e.g. connection refused) but not the connection string.
            throw new InvalidOperationException($"Database migration failed: {ex.GetBaseException().Message}", ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
