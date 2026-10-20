extern alias cloud;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using IndustrialIot.Edge;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;
using CloudMqttConnection = cloud::IndustrialIot.Cloud.Messaging.CloudMqttConnection;
using CloudProgram = cloud::Program;

namespace IndustrialIot.Deployment.Tests;

/// <summary>
/// The whole telemetry path with real parts: an Edge host polling 100 tags every second, Mosquitto with the
/// repository's configuration and ACL, the Cloud host and PostgreSQL. "Exactly once" is checked as a set: every
/// sequence the Edge produced before a snapshot is stored once in PostgreSQL, none missing.
/// </summary>
[Trait("Category", "Slow")]
public sealed class EndToEndTelemetryTests(ITestOutputHelper output)
{
    // Runs BrokerFixture's static constructor, which pins the Testcontainers resource reaper image, before the
    // first container of this class starts (reading the Image constant alone does not run it).
    static EndToEndTelemetryTests() => RuntimeHelpers.RunClassConstructor(typeof(BrokerFixture).TypeHandle);

    private const string EdgeId = "line1-edge01";
    private const int TagCount = 100;
    private const int MinDrainPerSecond = 200;
    private static readonly TimeSpan CloudOutage = TimeSpan.FromSeconds(40);

    [Fact]
    public async Task RepoBroker_NormalRunAndCloudOutage_EveryProducedSampleStoredOnce()
    {
        using var secrets = SecretsDirectory.Create("--edge", EdgeId);
        await using var database = await StartPostgresAsync();
        await using var broker = await BrokerFixture.StartAsync(secrets);
        await using var run = await Run.StartAsync(broker.Port, secrets, database.GetConnectionString());

        // Phase A: normal operation for 60 s.
        await Task.Delay(TimeSpan.FromSeconds(60));
        var producedA = await run.NextSequenceAsync();
        Assert.True(producedA >= 50 * TagCount, $"only {producedA} samples produced in 60 s");
        await run.WaitUntilCloudHoldsAllAsync(producedA, TimeSpan.FromSeconds(30));
        await run.WaitUntilEdgeBufferEmptyAsync(TimeSpan.FromSeconds(15));

        // Phase B: the Cloud is gone for 40 s while the Edge keeps polling and stays connected to the broker.
        await run.StopCloudAsync();
        await Task.Delay(CloudOutage);
        await run.StartCloudAsync();
        var backlog = await run.Store.CountAsync(default);
        var producedAtRestart = await run.NextSequenceAsync();
        var drain = Stopwatch.StartNew();
        await run.WaitUntilEdgeBufferEmptyAsync(TimeSpan.FromSeconds(120));
        drain.Stop();
        var producedDuringDrain = await run.NextSequenceAsync() - producedAtRestart;
        var gross = (backlog + producedDuringDrain) / drain.Elapsed.TotalSeconds;
        var drainLine = string.Create(CultureInfo.InvariantCulture,
            $"drain rate: {gross:F0} samples/s (backlog {backlog}, produced {producedDuringDrain}, {drain.Elapsed.TotalSeconds:F1} s)");
        output.WriteLine(drainLine);
        Console.WriteLine(drainLine); // also on the console: xUnit shows test output only for failed tests

        var producedB = await run.NextSequenceAsync();
        await run.WaitUntilCloudHoldsAllAsync(producedB, TimeSpan.FromSeconds(60));
        Assert.True(backlog > 1_000, $"the outage left only {backlog} samples queued");
        Assert.True(gross >= MinDrainPerSecond, $"drain rate {gross:F0}/s is below {MinDrainPerSecond}/s");
        Assert.DoesNotContain("dropped", await broker.LogsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CappedBroker_DropsQueuedMessages_EdgeStillDeliversEverySample()
    {
        using var secrets = SecretsDirectory.Create("--edge", EdgeId);
        await using var database = await StartPostgresAsync();
        await using var broker = await CappedBroker.StartAsync(secrets, maxQueuedMessages: 1000);
        await using var run = await Run.StartAsync(broker.Port, secrets, database.GetConnectionString());

        await Task.Delay(TimeSpan.FromSeconds(5));
        await run.StopCloudAsync();
        await Task.Delay(CloudOutage); // > 1 500 samples, past the second resend: the queue overflows
        await run.StartCloudAsync();
        var backlog = await run.Store.CountAsync(default);
        await run.WaitUntilEdgeBufferEmptyAsync(TimeSpan.FromSeconds(120));

        // The broker really dropped messages queued for the offline Cloud; the Edge still had every sample and resent it.
        Assert.True(backlog > 1_000, $"the outage left only {backlog} samples queued");
        Assert.Contains("Outgoing messages are being dropped for client iiot-cloud", await broker.LogsAsync(), StringComparison.Ordinal);
        var produced = await run.NextSequenceAsync();
        await run.WaitUntilCloudHoldsAllAsync(produced, TimeSpan.FromSeconds(60));
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        return container;
    }

    /// <summary>One Edge and one (restartable) Cloud, both talking to the broker on 127.0.0.1.</summary>
    private sealed class Run : IAsyncDisposable
    {
        private readonly int _brokerPort;
        private readonly SecretsDirectory _secrets;
        private readonly string _connectionString;
        private readonly string _edgeRoot = Path.Combine(Path.GetTempPath(), "iiot-e2e-" + Guid.NewGuid().ToString("N"));
        private IHost? _edge;
        private CloudHost? _cloud;

        private Run(int brokerPort, SecretsDirectory secrets, string connectionString)
        {
            _brokerPort = brokerPort;
            _secrets = secrets;
            _connectionString = connectionString;
        }

        public TelemetryStore Store => _edge!.Services.GetRequiredService<TelemetryStore>();

        private string EdgeDatabase => Path.Combine(_edgeRoot, "edge.db");

        public static async Task<Run> StartAsync(int brokerPort, SecretsDirectory secrets, string connectionString)
        {
            var run = new Run(brokerPort, secrets, connectionString);
            try
            {
                await run.StartCloudAsync();
                await run.StartEdgeAsync();
                return run;
            }
            catch
            {
                await run.DisposeAsync(); // no host keeps reconnecting in the background after a failed start
                throw;
            }
        }

        public async Task StartCloudAsync()
        {
            _cloud = new CloudHost(_brokerPort, _secrets, _connectionString);
            var connection = _cloud.Services.GetRequiredService<CloudMqttConnection>();
            await WaitUntilAsync(() => Task.FromResult(connection.IsConnected), TimeSpan.FromSeconds(30), "Cloud connected");
        }

        public async Task StopCloudAsync()
        {
            if (_cloud is not null)
            {
                await _cloud.DisposeAsync();
                _cloud = null;
            }
        }

        public async Task<long> NextSequenceAsync() => long.Parse(await EdgeMetaAsync("next_sequence"), CultureInfo.InvariantCulture);

        public async Task WaitUntilEdgeBufferEmptyAsync(TimeSpan timeout) =>
            await WaitUntilAsync(async () => await Store.CountAsync(default) == 0, timeout, "Edge buffer empty", TimeSpan.FromMilliseconds(10));

        /// <summary>Every sequence 0..produced-1 of the Edge's stream is in PostgreSQL exactly once.</summary>
        public async Task WaitUntilCloudHoldsAllAsync(long produced, TimeSpan timeout)
        {
            var stream = Guid.Parse(await EdgeMetaAsync("stream_id"));
            (long Rows, long Distinct, long Min, long Max) found = default;
            await WaitUntilAsync(
                async () =>
                {
                    found = await CloudSequencesAsync(stream, produced);
                    return found.Distinct == produced;
                },
                timeout,
                $"Cloud holds all {produced} samples");
            Assert.Equal((produced, produced, 0L, produced - 1), found);
        }

        public async ValueTask DisposeAsync()
        {
            await StopCloudAsync();
            if (_edge is not null)
            {
                await _edge.StopAsync();
                _edge.Dispose();
            }

            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_edgeRoot, recursive: true);
            }
            catch (IOException)
            {
                // A file may still be closing; the OS temp cleanup will take it.
            }
        }

        private async Task StartEdgeAsync()
        {
            Directory.CreateDirectory(_edgeRoot);
            var settings = new Dictionary<string, string?>
            {
                ["Edge:EdgeId"] = EdgeId,
                ["Edge:Plc:Host"] = "127.0.0.1", // unused: the driver below replaces the PLC
                ["Edge:Poll:IntervalMs"] = "1000",
                ["Edge:Buffer:Path"] = EdgeDatabase,
                ["Edge:Mqtt:Host"] = "127.0.0.1", // not "localhost": on Docker Desktop that is ::1 at ~23 publishes/s
                ["Edge:Mqtt:Port"] = _brokerPort.ToString(CultureInfo.InvariantCulture),
                ["Edge:Mqtt:Username"] = EdgeId,
                ["Edge:Mqtt:Password"] = File.ReadAllText(_secrets.File($"passwords/{EdgeId}.txt")),
                ["Edge:Mqtt:CaCertificatePath"] = _secrets.File("ca.pem"),
                ["Edge:Mqtt:ReconnectMinDelayMs"] = "200",
                ["Edge:Mqtt:ReconnectMaxDelayMs"] = "1000",
                ["Edge:Mqtt:AckTimeoutSeconds"] = "5",
                ["Edge:Monitor:Enabled"] = "false",
            };
            for (var i = 0; i < TagCount; i++)
            {
                settings[$"Edge:Tags:{i}:TagId"] = $"Line1.Tag{i:D3}";
                settings[$"Edge:Tags:{i}:Address"] = $"DB1.DBW{i * 2}";
                settings[$"Edge:Tags:{i}:DataType"] = "Int";
            }

            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _edgeRoot });
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddEdgeAgent(builder.Configuration);
            builder.Services.AddSingleton<IPlcDriver, SimulatedPlc>();
            _edge = builder.Build();
            await _edge.StartAsync();
        }

        private async Task<string> EdgeMetaAsync(string key)
        {
            await using var connection = new SqliteConnection($"Data Source={EdgeDatabase};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        private async Task<(long Rows, long Distinct, long Min, long Max)> CloudSequencesAsync(Guid stream, long below)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*), COUNT(DISTINCT sequence), COALESCE(MIN(sequence), -1), COALESCE(MAX(sequence), -1) " +
                "FROM telemetry WHERE edge_id = @edge AND stream_id = @stream AND sequence < @below";
            command.Parameters.AddWithValue("edge", EdgeId);
            command.Parameters.AddWithValue("stream", stream);
            command.Parameters.AddWithValue("below", below);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }
    }

    /// <summary>The real Cloud host, configured in code only (nothing comes from a copied appsettings.json).</summary>
    private sealed class CloudHost(int brokerPort, SecretsDirectory secrets, string connectionString) : WebApplicationFactory<CloudProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var settings = new Dictionary<string, string>
            {
                ["Cloud:Database:ConnectionString"] = connectionString,
                ["Cloud:Edges:0:EdgeId"] = EdgeId,
                ["Cloud:Edges:0:DisplayName"] = "Line 1",
                ["Cloud:Auth:Authority"] = "https://idp.test/", // the test sends no API request, so no metadata is fetched
                ["Cloud:Auth:Audience"] = "api://iiot-cloud",
                ["Cloud:Mqtt:Host"] = "127.0.0.1",
                ["Cloud:Mqtt:Port"] = brokerPort.ToString(CultureInfo.InvariantCulture),
                ["Cloud:Mqtt:Username"] = "iiot-cloud",
                ["Cloud:Mqtt:Password"] = File.ReadAllText(secrets.File("passwords/iiot-cloud.txt")),
                ["Cloud:Mqtt:CaCertificatePath"] = secrets.File("ca.pem"),
            };
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }

    /// <summary>A PLC whose every tag reads a changing Int.</summary>
    private sealed class SimulatedPlc : IPlcDriver
    {
        private int _reads;

        public Task<object> ReadAsync(PlcAddress address, PlcDataType type, CancellationToken ct) =>
            Task.FromResult<object>((short)(Interlocked.Increment(ref _reads) % short.MaxValue));

        public Task WriteAsync(PlcAddress address, PlcDataType type, object value, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> IsReachableAsync(CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>Mosquitto with the repository's configuration except a smaller per-client queue.</summary>
    private sealed class CappedBroker : IAsyncDisposable
    {
        private readonly IContainer _container;
        private readonly string _configDirectory;

        private CappedBroker(IContainer container, string configDirectory)
        {
            _container = container;
            _configDirectory = configDirectory;
        }

        public int Port => _container.GetMappedPublicPort(8883);

        public static async Task<CappedBroker> StartAsync(SecretsDirectory secrets, int maxQueuedMessages)
        {
            var repoConfig = Path.Combine(Repo.Root, "deployments", "mosquitto");
            var configDirectory = Path.Combine(Path.GetTempPath(), "iiot-capped-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(configDirectory);
            var config = await File.ReadAllTextAsync(Path.Combine(repoConfig, "mosquitto.conf"));
            Assert.Contains("max_queued_messages 20000", config, StringComparison.Ordinal);
            var capped = Path.Combine(configDirectory, "mosquitto.conf");
            await File.WriteAllTextAsync(capped, config.Replace("max_queued_messages 20000",
                $"max_queued_messages {maxQueuedMessages.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal));

            var container = new ContainerBuilder(BrokerFixture.Image)
                .WithPortBinding(8883, assignRandomHostPort: true)
                .WithBindMount(capped, "/mosquitto/config/mosquitto.conf", AccessMode.ReadOnly)
                .WithBindMount(Path.Combine(repoConfig, "acl"), "/mosquitto/config/acl", AccessMode.ReadOnly)
                .WithBindMount(secrets.File("ca.pem"), "/mosquitto/config/secrets/ca.pem", AccessMode.ReadOnly)
                .WithBindMount(secrets.File("mosquitto/server.pem"), "/mosquitto/config/secrets/server.pem", AccessMode.ReadOnly)
                .WithBindMount(secrets.File("mosquitto/server.key"), "/mosquitto/config/secrets/server.key", AccessMode.ReadOnly)
                .WithBindMount(secrets.File("mosquitto/passwd"), "/mosquitto/config/secrets/passwd", AccessMode.ReadOnly)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
                .Build();
            await container.StartAsync();
            return new CappedBroker(container, configDirectory);
        }

        public async Task<string> LogsAsync()
        {
            var (stdout, stderr) = await _container.GetLogsAsync();
            return stdout + stderr;
        }

        public async ValueTask DisposeAsync()
        {
            await _container.DisposeAsync();
            Directory.Delete(_configDirectory, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what, TimeSpan? poll = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < timeout, $"timed out after {timeout.TotalSeconds:F0} s waiting for: {what}");
            await Task.Delay(poll ?? TimeSpan.FromMilliseconds(200));
        }
    }
}
