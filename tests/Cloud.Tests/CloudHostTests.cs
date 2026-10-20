using System.Diagnostics;
using System.Net;
using System.Text.Json;
using IndustrialIot.Cloud.Data;
using IndustrialIot.Cloud.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Cloud.Tests;

[Collection(CloudCollection.Name)]
public class CloudHostTests(PostgresFixture database)
{
    [Fact]
    public async Task ValidConfig_MigratesAndServesHealth()
    {
        await using var host = new CloudTestHost(database);
        using var client = host.CreateClient();

        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("live", JsonDocument.Parse(await live.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());

        await WaitUntilAsync(async () => (await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK, "ready");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'public'")
            .ToListAsync();
        Assert.Contains("telemetry", tables);
        Assert.Contains("latest_values", tables);
        Assert.Contains("commands", tables);

        var indexes = await db.Database
            .SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'")
            .ToListAsync();
        Assert.Contains("ix_telemetry_edge_tag_time", indexes);
        Assert.Contains("ix_telemetry_received", indexes);
        Assert.Contains("ux_commands_issuer_key", indexes);
        Assert.Contains("ix_commands_status_expiry", indexes);
        Assert.Contains("ix_commands_edge_created", indexes);
    }

    [Theory]
    [InlineData("Cloud:Edges:0:EdgeId", "a b", "Cloud:Edges:0:EdgeId")]
    [InlineData("Cloud:Edges:1:EdgeId", "edge-a", "Cloud:Edges:1:EdgeId")] // duplicate
    [InlineData("Cloud:Commands:DefaultTtlSeconds", "700", "Cloud:Commands:DefaultTtlSeconds")]
    [InlineData("Cloud:Retention:TelemetryDays", "0", "Cloud:Retention:TelemetryDays")]
    [InlineData("Cloud:Health:RefreshSeconds", "0", "Cloud:Health:RefreshSeconds")]
    [InlineData("Cloud:ForwardedHeaders:KnownProxies:0", "not-an-ip", "Cloud:ForwardedHeaders:KnownProxies:0")]
    [InlineData("Cloud:ForwardedHeaders:KnownProxies:0", "10.1", "Cloud:ForwardedHeaders:KnownProxies:0")] // shorthand IPv4
    [InlineData("Cloud:Database:ConnectionString", "", "Cloud:Database:ConnectionString")] // "" also hides an env var on the machine
    public async Task InvalidConfig_FailsAtStart(string key, string value, string expectedKey)
    {
        await using var host = new CloudTestHost(database, new Dictionary<string, string?> { [key] = value });

        var error = Record.Exception(() => host.CreateClient());

        var validation = Find<OptionsValidationException>(error);
        Assert.NotNull(validation);
        Assert.Contains(expectedKey, validation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseUnreachable_FailsAtStart()
    {
        await using var host = new CloudTestHost(database, new Dictionary<string, string?>
        {
            ["Cloud:Database:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=nothing;Username=x;Password=y;Timeout=3",
        });

        var error = Record.Exception(() => host.CreateClient());

        var migration = Find<InvalidOperationException>(error, e => e.Message.StartsWith("Database migration failed", StringComparison.Ordinal));
        Assert.NotNull(migration);
        Assert.DoesNotContain("Password=y", migration.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_UsesCachedState()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = new CloudTestHost(
            database,
            new Dictionary<string, string?> { ["Cloud:Health:RefreshSeconds"] = "7" }, // not the default, so the option is proven wired
            services => services.AddSingleton<TimeProvider>(clock));
        using var client = host.CreateClient();
        var monitor = host.Services.GetServices<IHostedService>().OfType<Health.ReadinessMonitor>().Single();
        var refresh = TimeSpan.FromSeconds(7);

        await WaitUntilAsync(async () => (await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK, "first refresh");
        await WaitUntilAsync(() => Task.FromResult(monitor.WaitsArmed >= 1), "monitor waiting");

        // Probes are answered from memory: no database command, however many requests.
        var before = host.DatabaseFaults.CommandCount;
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        }

        Assert.Equal(before, host.DatabaseFaults.CommandCount);

        // An outage shows up exactly at the next refresh: one tick earlier the cached state is still "ready".
        host.DatabaseFaults.Enabled = true;
        clock.Advance(refresh - TimeSpan.FromTicks(1));
        await Task.Delay(300);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        clock.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(async () => (await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.ServiceUnavailable,
            "not ready after one refresh period");

        // Recovery likewise needs exactly one more refresh period.
        await WaitUntilAsync(() => Task.FromResult(monitor.WaitsArmed >= 2), "monitor waiting again");
        host.DatabaseFaults.Enabled = false;
        clock.Advance(refresh - TimeSpan.FromTicks(1));
        await Task.Delay(300);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        clock.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(async () => (await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK,
            "ready after one more refresh period");
    }

    private static T? Find<T>(Exception? error, Func<T, bool>? match = null)
        where T : Exception
    {
        var pending = new Stack<Exception>();
        if (error is not null)
        {
            pending.Push(error);
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is T typed && (match is null || match(typed)))
            {
                return typed;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return null;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), $"timed out waiting for: {what}");
            await Task.Delay(50);
        }
    }
}
