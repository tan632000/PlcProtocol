using Microsoft.EntityFrameworkCore;

namespace IndustrialIot.Cloud.Data;

/// <summary>The central database. Names are snake_case and set explicitly, so migrations read like plain SQL.</summary>
public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : DbContext(options)
{
    public DbSet<TelemetryRow> Telemetry => Set<TelemetryRow>();

    public DbSet<LatestValueRow> LatestValues => Set<LatestValueRow>();

    public DbSet<CommandRow> Commands => Set<CommandRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TelemetryRow>(e =>
        {
            e.ToTable("telemetry");
            e.HasKey(t => new { t.EdgeId, t.StreamId, t.Sequence }).HasName("pk_telemetry");
            e.Property(t => t.EdgeId).HasColumnName("edge_id");
            e.Property(t => t.StreamId).HasColumnName("stream_id");
            e.Property(t => t.Sequence).HasColumnName("sequence");
            e.Property(t => t.TagId).HasColumnName("tag_id");
            e.Property(t => t.Value).HasColumnName("value").HasColumnType("jsonb");
            e.Property(t => t.Quality).HasColumnName("quality");
            e.Property(t => t.TimestampUtc).HasColumnName("timestamp_utc");
            e.Property(t => t.ReceivedUtc).HasColumnName("received_utc");
            e.HasIndex(t => new { t.EdgeId, t.TagId, t.TimestampUtc }).HasDatabaseName("ix_telemetry_edge_tag_time");
            e.HasIndex(t => t.ReceivedUtc).HasDatabaseName("ix_telemetry_received");

            // Last receive time per Edge stays one index probe even for an Edge that has been silent for weeks.
            e.HasIndex(t => new { t.EdgeId, t.ReceivedUtc }).HasDatabaseName("ix_telemetry_edge_received");
        });

        modelBuilder.Entity<LatestValueRow>(e =>
        {
            e.ToTable("latest_values");
            e.HasKey(l => new { l.EdgeId, l.TagId }).HasName("pk_latest_values");
            e.Property(l => l.EdgeId).HasColumnName("edge_id");
            e.Property(l => l.TagId).HasColumnName("tag_id");
            e.Property(l => l.Value).HasColumnName("value").HasColumnType("jsonb");
            e.Property(l => l.Quality).HasColumnName("quality");
            e.Property(l => l.TimestampUtc).HasColumnName("timestamp_utc");
            e.Property(l => l.StreamId).HasColumnName("stream_id");
            e.Property(l => l.Sequence).HasColumnName("sequence");
        });

        modelBuilder.Entity<CommandRow>(e =>
        {
            e.ToTable("commands");
            e.HasKey(c => c.CommandId).HasName("pk_commands");
            e.Property(c => c.CommandId).HasColumnName("command_id");
            e.Property(c => c.EdgeId).HasColumnName("edge_id");
            e.Property(c => c.TagId).HasColumnName("tag_id");
            e.Property(c => c.TargetValue).HasColumnName("target_value").HasColumnType("jsonb");
            e.Property(c => c.IssuedAtUtc).HasColumnName("issued_at_utc");
            e.Property(c => c.ExpiresAtUtc).HasColumnName("expires_at_utc");
            e.Property(c => c.IssuedBy).HasColumnName("issued_by");
            e.Property(c => c.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(c => c.RequestHash).HasColumnName("request_hash");
            e.Property(c => c.Status).HasColumnName("status");
            e.Property(c => c.Reason).HasColumnName("reason");
            e.Property(c => c.CompletedAtUtc).HasColumnName("completed_at_utc");
            e.Property(c => c.ResultReceivedUtc).HasColumnName("result_received_utc");
            e.Property(c => c.CreatedUtc).HasColumnName("created_utc");

            // Idempotency keys are unique forever per user.
            e.HasIndex(c => new { c.IssuedBy, c.IdempotencyKey })
                .IsUnique()
                .HasFilter("idempotency_key IS NOT NULL")
                .HasDatabaseName("ux_commands_issuer_key");
            e.HasIndex(c => new { c.Status, c.ExpiresAtUtc }).HasDatabaseName("ix_commands_status_expiry");
            e.HasIndex(c => new { c.EdgeId, c.CreatedUtc }).HasDatabaseName("ix_commands_edge_created");
        });
    }
}
