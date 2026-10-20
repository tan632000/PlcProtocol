using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IndustrialIot.Edge.Tests.Fakes;

/// <summary>
/// Builds the real Edge host (<see cref="EdgeServiceCollectionExtensions.AddEdgeAgent"/>) with valid
/// defaults for every <c>Edge:*</c> section, so each test only states what it changes. Every instance
/// gets its own temp content root, deleted on dispose. Later tasks add defaults for their sections here.
/// </summary>
internal sealed class EdgeTestHost : IAsyncDisposable
{
    private EdgeTestHost(IHost host, string contentRoot)
    {
        Host = host;
        ContentRoot = contentRoot;
    }

    public IHost Host { get; }

    public string ContentRoot { get; }

    public IServiceProvider Services => Host.Services;

    /// <param name="overrides">Configuration keys to set; a <c>null</c> value removes the key.</param>
    /// <param name="configureServices">Runs after <c>AddEdgeAgent</c>, e.g. to replace <c>IPlcDriver</c> or <c>TimeProvider</c>.</param>
    /// <param name="runAgentServices">
    /// False (default): only the database starter runs, so a test controls every read/write itself.
    /// True: every Edge hosted service (poller, ...) runs as in production.
    /// </param>
    public static EdgeTestHost Create(
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null,
        bool runAgentServices = false)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "edge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        var settings = DefaultSettings();
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                settings.Remove(key);
            }
            else
            {
                settings[key] = value;
            }
        }

        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(
                new HostApplicationBuilderSettings { ContentRootPath = contentRoot });
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddEdgeAgent(builder.Configuration);
            configureServices?.Invoke(builder.Services);
            if (!runAgentServices)
            {
                RemoveAgentHostedServices(builder.Services);
            }

            return new EdgeTestHost(builder.Build(), contentRoot);
        }
        catch
        {
            DeleteDirectory(contentRoot);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            Host.Dispose();
        }

        DeleteDirectory(ContentRoot);
    }

    /// <summary>Gets a running Edge hosted service, e.g. the poller.</summary>
    public T HostedService<T>()
        where T : IHostedService =>
        Services.GetServices<IHostedService>().OfType<T>().Single();

    /// <summary>
    /// Removes one Edge hosted service, e.g. the command listener when a test subscribes to the commands
    /// topic itself (only one handler per topic is allowed).
    /// </summary>
    public static void WithoutHostedService<T>(IServiceCollection services)
        where T : IHostedService
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }

    private static void RemoveAgentHostedServices(IServiceCollection services)
    {
        var agentServices = services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType != typeof(Buffering.EdgeDatabaseStarter))
            .ToList();
        foreach (var descriptor in agentServices)
        {
            services.Remove(descriptor);
        }
    }

    internal static void DeleteDirectory(string directory)
    {
        // Pooled SQLite connections keep the database file open after the host is gone.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file may still be closing; the OS temp cleanup will take it.
        }
    }

    // Configuration keys are case-insensitive, so overrides must be too.
    private static Dictionary<string, string?> DefaultSettings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Edge:EdgeId"] = "test-edge",

        // The real driver is registered but nothing listens here; starting the host performs no PLC I/O.
        ["Edge:Plc:Host"] = "127.0.0.1",
        ["Edge:Plc:Port"] = ClosedPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Edge:Plc:TimeoutMs"] = "500",

        ["Edge:Tags:0:TagId"] = "Line1.Speed",
        ["Edge:Tags:0:Address"] = "DB1.DBW2",
        ["Edge:Tags:0:DataType"] = "Int",

        // Relative: lands inside this instance's temp content root.
        ["Edge:Buffer:Path"] = "data/edge.db",

        ["Edge:Poll:IntervalMs"] = "1000",

        // No broker listens here unless a test points these at a TestMqttBroker.
        ["Edge:Mqtt:Host"] = "127.0.0.1",
        ["Edge:Mqtt:Port"] = ClosedPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Edge:Mqtt:Username"] = "test-edge",
        ["Edge:Mqtt:Password"] = "test-only-password",
        ["Edge:Mqtt:ReconnectMinDelayMs"] = "200",
        ["Edge:Mqtt:ReconnectMaxDelayMs"] = "1000",

        // Off unless a test turns it on with a free port: parallel hosts would fight over 5080.
        ["Edge:Monitor:Enabled"] = "false",
    };

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
