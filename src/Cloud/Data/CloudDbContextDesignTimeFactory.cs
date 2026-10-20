using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IndustrialIot.Cloud.Data;

/// <summary>
/// Used only by <c>dotnet ef</c> to create migrations: no host, no live database, no real connection string.
/// </summary>
internal sealed class CloudDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CloudDbContext>
{
    public CloudDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only")
            .Options);
}
