using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Edge.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IndustrialIot.Edge.Tests;

/// <summary>
/// One outer host owns the process lifetime (console or SCM); the supervisor rebuilds only the inner agent host when
/// settings are applied, rolls back to the previous settings when the new ones do not start, and recovers after a
/// crash in the middle of an apply.
/// </summary>
public sealed class EdgeSupervisorTests : IAsyncDisposable
{
    private readonly string _contentRoot = Directory.CreateTempSubdirectory("edge-supervisor-").FullName;
    private IHost? _outer;

    public async ValueTask DisposeAsync()
    {
        if (_outer is not null)
        {
            await _outer.StopAsync();
            _outer.Dispose();
        }

        EdgeTestHost.DeleteDirectory(_contentRoot);
    }

    [Fact]
    public async Task Apply_RebuildsTheInnerHost_OnTheNewSettings()
    {
        var supervisor = await StartAsync();
        var restarter = Restarter();
        var first = supervisor.Inner!;

        var applyId = Apply(restarter, Settings("New.One", "New.Two"));
        var status = await WaitForFinalAsync(restarter, applyId);

        Assert.Equal(ApplyState.Applied, status.State);
        Assert.Null(status.Error);
        Assert.NotSame(first, supervisor.Inner);
        Assert.Equal(["New.One", "New.Two"], Tags(supervisor.Inner!));
        Assert.Equal(Revision(Settings("New.One", "New.Two")), status.Revision);
        Assert.False(File.Exists(MarkerPath()));
    }

    [Fact]
    public async Task Apply_DisposesTheOldInnerHost_SoOnlyOnePlcConnectionRemains()
    {
        await using var plc = FakeS7Server.Start(FakeS7Mode.HandshakeThenRespond, readData: [0, 7]);
        var supervisor = await StartAsync(overrides: new()
        {
            ["Edge:Plc:Port"] = plc.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Edge:Poll:IntervalMs"] = "100",
        }, runAgentServices: true);
        await WaitUntilAsync(() => plc.OpenConnections == 1, "first PLC connection");
        var restarter = Restarter();
        var first = supervisor.Inner!;

        var bytes = Settings(plcPort: plc.Port, intervalMs: 100, tagIds: "New.One");
        var status = await WaitForFinalAsync(restarter, Apply(restarter, bytes));

        Assert.Equal(ApplyState.Applied, status.State);

        // Stopping alone is not enough: disposing releases the driver singleton (its connection) and the port.
        Assert.Throws<ObjectDisposedException>(() => first.Services.GetRequiredService<IPlcDriver>());
        await WaitUntilAsync(() => plc.AcceptCount >= 2, "second PLC connection");
        await Task.Delay(300);
        Assert.Equal(1, plc.OpenConnections);
        Assert.Equal(["New.One"], Tags(supervisor.Inner!));
    }

    [Fact]
    public async Task FailingStart_RestoresThePreviousFile_AndReportsRolledBack()
    {
        WriteSettings(Settings("Old.One"));
        var builds = 0;
        var supervisor = await StartAsync(configureInner: services =>
        {
            // Build 1: initial start on Old.One; build 2: the apply, which fails; build 3: the rollback.
            if (Interlocked.Increment(ref builds) == 2)
            {
                services.AddHostedService<ThrowingHostedService>();
            }
        });
        Assert.Equal(["Old.One"], Tags(supervisor.Inner!));
        var restarter = Restarter();

        var status = await WaitForFinalAsync(restarter, Apply(restarter, Settings("New.Broken")));

        Assert.Equal(ApplyState.RolledBack, status.State);
        Assert.NotNull(status.Error);
        Assert.DoesNotContain("seam", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Settings("Old.One"), File.ReadAllBytes(SettingsPath()));
        Assert.Equal(["Old.One"], Tags(supervisor.Inner!));
        Assert.Equal(Revision(Settings("Old.One")), status.Revision);
        Assert.False(File.Exists(MarkerPath()));
    }

    [Fact]
    public async Task MonitorPortTakenLongerThanTheBindRetry_RollsBack()
    {
        // Bind retry 2 s; the port is taken for 3 s from the apply's start: the apply fails after 2 s, the rollback
        // (retrying 2 s again) binds once the port is free at 3 s.
        var port = FreePort();
        await using var taker = new PortTaker(port, holdFor: TimeSpan.FromSeconds(3));
        var builds = 0;
        var supervisor = await StartAsync(
            overrides: MonitorOn(port),
            bindRetry: TimeSpan.FromSeconds(2),
            runAgentServices: true,
            configureInner: services =>
            {
                if (Interlocked.Increment(ref builds) == 2)
                {
                    PortTaker.InsertFirst(services, taker);
                }
            });
        var restarter = Restarter();

        var status = await WaitForFinalAsync(restarter, Apply(restarter, Settings("New.One")));

        Assert.Equal(ApplyState.RolledBack, status.State);
        Assert.Equal(3, builds);
        Assert.False(File.Exists(SettingsPath()));
        Assert.Equal(["Line1.Speed"], Tags(supervisor.Inner!));
    }

    [Fact]
    public async Task MonitorPortBusyShorterThanTheBindRetry_Applies()
    {
        var port = FreePort();
        await using var taker = new PortTaker(port, holdFor: TimeSpan.FromSeconds(1));
        var builds = 0;
        var supervisor = await StartAsync(
            overrides: MonitorOn(port),
            bindRetry: TimeSpan.FromSeconds(3),
            runAgentServices: true,
            configureInner: services =>
            {
                if (Interlocked.Increment(ref builds) == 2)
                {
                    PortTaker.InsertFirst(services, taker);
                }
            });
        var restarter = Restarter();

        var status = await WaitForFinalAsync(restarter, Apply(restarter, Settings("New.One")));

        Assert.Equal(ApplyState.Applied, status.State);
        Assert.Equal(2, builds);
        Assert.Equal(["New.One"], Tags(supervisor.Inner!));
    }

    [Fact]
    public async Task FirstTimeFileThatFails_IsDeleted_AndTheBaseSettingsRun()
    {
        var builds = 0;
        var supervisor = await StartAsync(configureInner: services =>
        {
            if (Interlocked.Increment(ref builds) == 2)
            {
                services.AddHostedService<ThrowingHostedService>();
            }
        });

        // A stale backup from an earlier session must not be restored: there was no file before this apply.
        File.WriteAllBytes(SettingsPath() + ".bak", Settings("Stale.Backup"));
        var restarter = Restarter();
        var status = await WaitForFinalAsync(restarter, Apply(restarter, Settings("New.Broken")));

        Assert.Equal(ApplyState.RolledBack, status.State);
        Assert.False(File.Exists(SettingsPath()));
        Assert.Equal(["Line1.Speed"], Tags(supervisor.Inner!));
        Assert.Equal(EdgeSettingsFile.NoFileRevision, status.Revision);
    }

    [Fact]
    public async Task RollbackThatAlsoFails_StopsTheOuterHostWithExitCode1()
    {
        var builds = 0;
        var supervisor = await StartAsync(configureInner: services =>
        {
            if (Interlocked.Increment(ref builds) >= 2)
            {
                services.AddHostedService<ThrowingHostedService>();
            }
        });
        var stopped = _outer!.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        var restarter = Restarter();

        Apply(restarter, Settings("New.Broken"));

        await WaitUntilAsync(() => stopped.IsCancellationRequested, "outer host stopping");
        Assert.Equal(1, supervisor.ExitCode);
    }

    [Fact]
    public async Task ColdStartWithMarker_AndAFailingFile_RestoresTheMarkerContents()
    {
        // A crash after the new file was written but before it started: the marker holds the previous bytes.
        WriteSettings(Settings("New.Broken"));
        WriteMarker(Settings("Old.One"));
        var builds = 0;

        var supervisor = await StartAsync(configureInner: services =>
        {
            if (Interlocked.Increment(ref builds) == 1)
            {
                services.AddHostedService<ThrowingHostedService>();
            }
        });

        Assert.Equal(Settings("Old.One"), File.ReadAllBytes(SettingsPath()));
        Assert.Equal(["Old.One"], Tags(supervisor.Inner!));
        Assert.False(File.Exists(MarkerPath()));
    }

    [Fact]
    public async Task ColdStartWithMarker_AndAGoodFile_KeepsTheFile_AndDropsTheMarker()
    {
        WriteSettings(Settings("New.Good"));
        WriteMarker(previous: null);

        var supervisor = await StartAsync();

        Assert.Equal(Settings("New.Good"), File.ReadAllBytes(SettingsPath()));
        Assert.Equal(["New.Good"], Tags(supervisor.Inner!));
        Assert.False(File.Exists(MarkerPath()));
    }

    [Fact]
    public async Task ColdStartWithoutMarker_AndAFailingStart_FailsTheOuterStart()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(configureInner: services =>
            services.AddHostedService<ThrowingHostedService>()));
    }

    [Fact]
    public async Task OuterStop_DuringARestart_EndsCleanly()
    {
        var builds = 0;
        var hanging = new HangingStartHostedService();
        var supervisor = await StartAsync(configureInner: services =>
        {
            // The initial start is quick; the apply's start hangs until the outer stop cancels it.
            if (Interlocked.Increment(ref builds) == 2)
            {
                services.AddSingleton<IHostedService>(hanging);
            }
        });
        var restarter = Restarter();
        Apply(restarter, Settings("New.One"));
        await hanging.Entered.WaitAsync(TimeSpan.FromSeconds(15));

        var clock = Stopwatch.StartNew();
        await _outer!.StopAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12), $"outer stop took {clock.Elapsed}");
        Assert.True(hanging.Cancelled, "the hanging start was not cancelled");
        Assert.Null(supervisor.Inner);

        // The new file was written before the stop: the marker stays so the next process start can roll back.
        Assert.True(File.Exists(MarkerPath()));
    }

    [Fact]
    public async Task SettingsFileThatCannotBeWritten_KeepsTheRunningHost_AndLaterAppliesStillWork()
    {
        WriteSettings(Settings("Old.One"));
        var supervisor = await StartAsync();
        var first = supervisor.Inner!;
        var restarter = Restarter();

        // A directory where the file belongs: renaming over it fails on Windows and on Linux alike (a read-only file
        // would not do: rename(2) replaces it on Linux, where CI runs).
        var parked = SettingsPath() + ".parked";
        File.Move(SettingsPath(), parked);
        Directory.CreateDirectory(SettingsPath());
        try
        {
            var status = await WaitForFinalAsync(restarter, Apply(restarter, Settings("New.One")));

            Assert.Equal(ApplyState.RolledBack, status.State);
            Assert.Equal(EdgeSupervisor.NotWrittenMessage, status.Error);
            Assert.Same(first, supervisor.Inner);
            Assert.Equal(["Old.One"], Tags(supervisor.Inner!));
            Assert.False(File.Exists(MarkerPath()));
        }
        finally
        {
            Directory.Delete(SettingsPath(), recursive: true);
            File.Move(parked, SettingsPath());
        }

        var next = await WaitForFinalAsync(restarter, Apply(restarter, Settings("New.Two")));
        Assert.Equal(ApplyState.Applied, next.State);
        Assert.Equal(["New.Two"], Tags(supervisor.Inner!));
    }

    [Fact]
    public async Task StatusPage_ReportsSettingsApply_ForTheLatestApply()
    {
        var port = FreePort();
        await StartAsync(overrides: MonitorOn(port), runAgentServices: true);
        var restarter = Restarter();

        var applyId = Apply(restarter, Settings("New.One"));
        var status = await WaitForFinalAsync(restarter, applyId);
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        using var json = JsonDocument.Parse(await http.GetStringAsync("/api/status"));
        var apply = json.RootElement.GetProperty("settingsApply");

        Assert.Equal(ApplyState.Applied, status.State);
        Assert.Equal("applied", apply.GetProperty("state").GetString());
        Assert.Equal(applyId, apply.GetProperty("applyId").GetString());
        Assert.Equal(Revision(Settings("New.One")), apply.GetProperty("revision").GetString());
        Assert.Equal(JsonValueKind.Null, apply.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task InnerHosts_DoNotOwnTheProcessLifetime_AndShareTheSupervisorState()
    {
        var supervisor = await StartAsync();
        var restarter = Restarter();
        var firstLifetime = supervisor.Inner!.Services.GetRequiredService<IHostLifetime>();
        var firstRestarter = supervisor.Inner.Services.GetRequiredService<EdgeRestarter>();

        await WaitForFinalAsync(restarter, Apply(restarter, Settings("New.One")));

        foreach (var lifetime in new[] { firstLifetime, supervisor.Inner!.Services.GetRequiredService<IHostLifetime>() })
        {
            Assert.DoesNotContain("WindowsServiceLifetime", lifetime.GetType().Name);
            Assert.DoesNotContain("ConsoleLifetime", lifetime.GetType().Name);
        }

        Assert.Same(restarter, firstRestarter);
        Assert.Same(restarter, supervisor.Inner.Services.GetRequiredService<EdgeRestarter>());
    }

    [Fact]
    public async Task SecondRequest_WhileOneIsPending_IsRefused()
    {
        await StartAsync();
        var restarter = Restarter();

        Assert.True(restarter.TryRequest(Settings("New.One"), out var first));
        Assert.False(restarter.TryRequest(Settings("New.Two"), out _));

        restarter.Release(first);
        Assert.Equal(ApplyState.Applied, (await WaitForFinalAsync(restarter, first)).State);
        Assert.True(restarter.TryRequest(Settings("New.Three"), out var third));
        restarter.Release(third);
        await WaitForFinalAsync(restarter, third);
    }

    private async Task<EdgeSupervisor> StartAsync(
        Dictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureInner = null,
        TimeSpan? bindRetry = null,
        bool runAgentServices = false)
    {
        var settings = BaseSettings();
        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _contentRoot });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddEdgeSupervisor(options =>
        {
            options.BindRetry = bindRetry ?? TimeSpan.FromSeconds(1);
            options.ConfigureInner = services =>
            {
                if (!runAgentServices)
                {
                    // Only the database starter runs, as in EdgeTestHost: no PLC or broker traffic.
                    foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType != typeof(Buffering.EdgeDatabaseStarter)).ToList())
                    {
                        services.Remove(descriptor);
                    }
                }

                configureInner?.Invoke(services);
            };
        });

        var outer = builder.Build();
        try
        {
            await outer.StartAsync();
        }
        catch
        {
            outer.Dispose();
            throw;
        }

        _outer = outer;
        return outer.Services.GetRequiredService<EdgeSupervisor>();
    }

    private EdgeRestarter Restarter() => _outer!.Services.GetRequiredService<EdgeRestarter>();

    private static string Apply(EdgeRestarter restarter, byte[] bytes)
    {
        Assert.True(restarter.TryRequest(bytes, out var applyId));
        restarter.Release(applyId);
        return applyId;
    }

    private static async Task<ApplyStatus> WaitForFinalAsync(EdgeRestarter restarter, string applyId)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var status = restarter.Status;
            if (status.ApplyId == applyId && status.State != ApplyState.Applying)
            {
                return status;
            }

            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), $"apply {applyId} did not finish (state {status.State})");
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), $"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    private static IReadOnlyList<string> Tags(IHost inner) =>
        inner.Services.GetRequiredService<PlcTagCatalog>().Tags.Select(t => t.TagId).ToList();

    private static string Revision(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private string SettingsPath() => Path.Combine(_contentRoot, "data", "edge.settings.json");

    private string MarkerPath() => Path.Combine(_contentRoot, "data", "edge.settings.pending");

    private void WriteSettings(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath())!);
        File.WriteAllBytes(SettingsPath(), bytes);
    }

    private void WriteMarker(byte[]? previous)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath())!);
        File.WriteAllText(MarkerPath(), JsonSerializer.Serialize(new { Previous = previous is null ? null : Convert.ToBase64String(previous) }));
    }

    private static byte[] Settings(params string[] tagIds) => Settings(plcPort: null, intervalMs: 1000, tagIds);

    private static byte[] Settings(int? plcPort, int intervalMs, params string[] tagIds)
    {
        var tags = string.Join(",", tagIds.Select((id, i) => $$"""{"TagId":"{{id}}","Address":"DB1.DBW{{i * 2}}","DataType":"Int"}"""));
        var port = plcPort is null ? "" : $",\"Port\":{plcPort}";
        return Encoding.UTF8.GetBytes(
            "{\"SchemaVersion\":1,\"Edge\":{\"Plc\":{\"Host\":\"127.0.0.1\"" + port + ",\"TimeoutMs\":500},"
            + "\"Poll\":{\"IntervalMs\":" + intervalMs + "},\"Tags\":[" + tags + "]}}");
    }

    private static Dictionary<string, string?> BaseSettings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Edge:EdgeId"] = "supervisor-test",
        ["Edge:Plc:Host"] = "127.0.0.1",
        ["Edge:Plc:Port"] = ClosedPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Edge:Plc:TimeoutMs"] = "500",
        ["Edge:Tags:0:TagId"] = "Line1.Speed",
        ["Edge:Tags:0:Address"] = "DB1.DBW2",
        ["Edge:Tags:0:DataType"] = "Int",
        ["Edge:Buffer:Path"] = "data/edge.db",
        ["Edge:Poll:IntervalMs"] = "1000",
        ["Edge:Mqtt:Host"] = "127.0.0.1",
        ["Edge:Mqtt:Port"] = ClosedPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Edge:Mqtt:Username"] = "supervisor-test",
        ["Edge:Mqtt:Password"] = "test-only-password",
        ["Edge:Mqtt:ReconnectMinDelayMs"] = "200",
        ["Edge:Mqtt:ReconnectMaxDelayMs"] = "1000",
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

    private static int FreePort() => ClosedPort();

    private static Dictionary<string, string?> MonitorOn(int port) => new()
    {
        ["Edge:Monitor:Enabled"] = "true",
        ["Edge:Monitor:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Inserted first into the apply's inner host, so it starts before the monitor: takes the monitor port (the old
    /// host has already released it) and frees it after <c>holdFor</c>, whatever happens to the host meanwhile.
    /// </summary>
    private sealed class PortTaker(int port, TimeSpan holdFor) : IHostedService, IAsyncDisposable
    {
        private TcpListener? _listener;
        private Task _release = Task.CompletedTask;

        public static void InsertFirst(IServiceCollection services, PortTaker taker) =>
            services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(taker));

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            listener.Start();
            _listener = listener;
            _release = Task.Delay(holdFor, CancellationToken.None).ContinueWith(_ => listener.Stop(), TaskScheduler.Default);
            return Task.CompletedTask;
        }

        // The port stays taken on stop: the rollback must meet it too until holdFor has passed.
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            await _release;
            _listener?.Stop();
        }
    }

    private sealed class ThrowingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("seam: this inner host must not start");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HangingStartHostedService : IHostedService
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public bool Cancelled { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
