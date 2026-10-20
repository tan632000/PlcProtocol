using Npgsql;
using Testcontainers.PostgreSql;

namespace IndustrialIot.Cloud.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL container for the whole test run; every Cloud test host gets its own fresh database in it.
/// Outages are simulated with <see cref="DatabaseFaultInterceptor"/>, never by stopping this shared container.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A connection string to a database that does not exist yet; migrations create it.</summary>
    public string NewDatabaseConnectionString() =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "cloud_" + Guid.NewGuid().ToString("N"),

            // The default 15 s is too short when other test projects load the same Docker host at the same time
            // (seen as "Database migration failed: The operation has timed out" on a 4-CPU Docker Desktop).
            Timeout = 60,

            // Same cause for migration commands under load ("Timeout during reading attempt" after the default 30 s).
            CommandTimeout = 120,
        }.ConnectionString;
}

/// <summary>Every Cloud test class joins this collection, so they share one container.</summary>
[CollectionDefinition(Name)]
public sealed class CloudCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "cloud";
}
