using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Settings;

public sealed class EdgeSupervisorOptions
{
    /// <summary>How long an inner host started by an apply keeps retrying the monitor port before failing.</summary>
    public TimeSpan BindRetry { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Shutdown timeout of every inner host.</summary>
    public TimeSpan InnerShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long an apply waits for its HTTP response to complete before going ahead anyway.</summary>
    public TimeSpan ReleaseTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Test seam: runs on every inner host's services after <c>AddEdgeAgent</c>.</summary>
    public Action<IServiceCollection>? ConfigureInner { get; set; }
}

/// <summary>How the monitor treats a taken port: log and run without the page, or retry and then fail the start.</summary>
public sealed record MonitorStartPolicy(TimeSpan BindRetry, bool FailWhenUnavailable)
{
    /// <summary>A process start keeps today's behaviour: the Edge runs without the page.</summary>
    public static MonitorStartPolicy ProcessStart { get; } = new(TimeSpan.Zero, FailWhenUnavailable: false);
}

/// <summary>
/// State that must outlive inner-host rebuilds, created once in the outer host and registered as the same instances
/// in every inner host. Later settings features add their singletons here.
/// </summary>
public sealed class EdgeSharedServices(EdgeRestarter restarter, SettingsPassword password, PlcTestLimiter testLimiter)
{
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton(restarter));

        // The lockout must survive applies, or a rebuild would reset the count of wrong passwords.
        services.Replace(ServiceDescriptor.Singleton(password));
        services.Replace(ServiceDescriptor.Singleton(testLimiter));
    }
}

public static class EdgeSupervisorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the supervisor in the outer host, which owns the process lifetime (console or Windows service). The
    /// agent itself runs in inner hosts the supervisor builds; never call <c>AddEdgeAgent</c> on the outer host.
    /// </summary>
    public static IServiceCollection AddEdgeSupervisor(this IServiceCollection services, Action<EdgeSupervisorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<EdgeSupervisorOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<EdgeRestarter>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SettingsPassword>();
        services.TryAddSingleton<PlcTestLimiter>();
        services.TryAddSingleton<EdgeSharedServices>();
        services.TryAddSingleton<EdgeSupervisor>();
        services.AddHostedService(provider => provider.GetRequiredService<EdgeSupervisor>());
        return services;
    }
}

/// <summary>
/// Runs the Edge agent in an inner host and rebuilds it when settings are applied. A rebuild writes a pending marker
/// with the previous settings, writes the new file, stops and disposes the old host (releasing its PLC connection and
/// port), then starts a new one. A start that throws, or a monitor that cannot listen, rolls back to the previous
/// settings; if those do not start either, the process stops with exit code 1. The marker lets the next process start
/// roll back after a crash in the middle of an apply.
/// </summary>
public sealed class EdgeSupervisor(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime lifetime,
    IHostLifetime hostLifetime,
    EdgeRestarter restarter,
    EdgeSharedServices shared,
    IOptions<EdgeSupervisorOptions> options,
    ILogger<EdgeSupervisor> logger) : IHostedService, IAsyncDisposable
{
    internal const string RolledBackMessage = "The saved settings did not start; the previous settings were restored.";
    internal const string NotWrittenMessage = "The saved settings could not be written; the running settings were kept.";
    internal const string FatalMessage = "Neither the saved nor the previous settings started; the Edge is stopping.";

    private readonly CancellationTokenSource _stopping = new();
    private IHost? _inner;
    private Task _loop = Task.CompletedTask;
    private volatile bool _innerStopExpected;

    /// <summary>The running inner host; null before start, after stop, or after a fatal apply.</summary>
    public IHost? Inner => Volatile.Read(ref _inner);

    /// <summary>The process exit code: 1 after an apply whose rollback also failed, otherwise 0.</summary>
    public int ExitCode { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settingsPath = EdgeSettingsFile.ResolvePath(configuration, EdgeSettingsFile.ResolveContentRoot(configuration));
        var markerPath = MarkerPath(settingsPath);

        IHost inner;
        if (TryReadMarker(markerPath, out var previous))
        {
            // The last process died during an apply: the file may hold settings that never started.
            try
            {
                inner = await StartInnerAsync(MonitorStartPolicy.ProcessStart, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Settings left by an interrupted apply did not start; restoring the previous settings.");
                RestoreFile(settingsPath, previous);
                inner = await StartInnerAsync(MonitorStartPolicy.ProcessStart, cancellationToken).ConfigureAwait(false);
            }

            File.Delete(markerPath);
        }
        else
        {
            // No apply in flight: a failing start fails the process as it always did.
            inner = await StartInnerAsync(MonitorStartPolicy.ProcessStart, cancellationToken).ConfigureAwait(false);
        }

        SetInner(inner);
        restarter.Initialize(inner.Services.GetRequiredService<EdgeSettingsFile>().Revision);
        _loop = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Outer shutdown timeout, or a loop failure already reported: stop the inner host anyway.
            if (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The settings apply loop failed.");
            }
        }

        await StopInnerAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopInnerAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task LoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested && ExitCode == 0)
        {
            ApplyRequest request;
            try
            {
                request = await restarter.WaitForRequestAsync(options.Value.ReleaseTimeout, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await ApplyAsync(request, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Outer stop during the apply: the marker stays, so the next start can still roll back.
                return;
            }
            catch (Exception ex)
            {
                // Anything ApplyAsync did not handle leaves no trustworthy agent running.
                Fatal(request, ex);
            }
        }
    }

    private async Task ApplyAsync(ApplyRequest request, CancellationToken stopping)
    {
        var current = Inner!.Services.GetRequiredService<EdgeSettingsFile>();
        var previous = current.Bytes;
        var markerPath = MarkerPath(current.Path);
        var applying = new MonitorStartPolicy(options.Value.BindRetry, FailWhenUnavailable: true);

        try
        {
            WriteMarker(markerPath, previous);
            RestoreFile(current.Path, request.Bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing stopped yet: put the file back, keep the running agent, report the save as not applied.
            logger.LogError(ex, "The saved settings could not be written to {Path}; keeping the running settings.", current.Path);
            TryRun(() => RestoreFile(current.Path, previous));
            TryRun(() => File.Delete(markerPath));
            restarter.Complete(request.ApplyId, ApplyState.RolledBack, current.Revision, NotWrittenMessage);
            return;
        }

        await StopInnerAsync().ConfigureAwait(false);

        try
        {
            var inner = await StartInnerAsync(applying, stopping).ConfigureAwait(false);
            SetInner(inner);
            TryRun(() => File.Delete(markerPath));
            restarter.Complete(request.ApplyId, ApplyState.Applied, Revision(inner), null);
            logger.LogInformation("Settings applied (revision {Revision}).", Revision(inner));
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            logger.LogError(ex, "The saved settings did not start; restoring the previous settings.");
        }

        // A failed restore is logged; the start below then fails too and ends in Fatal.
        TryRun(() => RestoreFile(current.Path, previous));
        try
        {
            var inner = await StartInnerAsync(applying, stopping).ConfigureAwait(false);
            SetInner(inner);
            TryRun(() => File.Delete(markerPath));
            restarter.Complete(request.ApplyId, ApplyState.RolledBack, Revision(inner), RolledBackMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
        {
            Fatal(request, ex);
        }
    }

    /// <summary>
    /// No agent is running and none could be started: stop the process with exit code 1. Under the SCM the code is
    /// also reported as the service's exit code, so the stop is recorded as a failure, not a clean stop.
    /// </summary>
    private void Fatal(ApplyRequest request, Exception ex)
    {
        logger.LogCritical(ex, "No Edge settings could be started; stopping the Edge.");
        ExitCode = 1;
        if (OperatingSystem.IsWindows())
        {
            ReportServiceExitCode(hostLifetime, 1);
        }

        restarter.Complete(request.ApplyId, ApplyState.RolledBack, EdgeSettingsFile.NoFileRevision, FatalMessage);
        lifetime.StopApplication();
    }

    // Separate method: ServiceBase is only touched on Windows (the Linux CI runs Fatal too).
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ReportServiceExitCode(IHostLifetime hostLifetime, int exitCode)
    {
        if (hostLifetime is System.ServiceProcess.ServiceBase service)
        {
            service.ExitCode = exitCode;
        }
    }

    private void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Settings file housekeeping failed.");
        }
    }

    private async Task<IHost> StartInnerAsync(MonitorStartPolicy monitorPolicy, CancellationToken ct)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = environment.ContentRootPath,
            EnvironmentName = environment.EnvironmentName,
            ApplicationName = environment.ApplicationName,
        });
        builder.Configuration.AddConfiguration(configuration);

        // The outer host owns the process lifetime and the log providers (Event Log under the SCM).
        builder.Services.Replace(ServiceDescriptor.Singleton(loggerFactory));
        builder.Services.Replace(ServiceDescriptor.Singleton<IHostLifetime, InnerHostLifetime>());
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.Value.InnerShutdownTimeout);
        builder.Services.AddSingleton(monitorPolicy);
        shared.Register(builder.Services);

        IHost? host = null;
        try
        {
            builder.Services.AddEdgeAgent(builder.Configuration);
            options.Value.ConfigureInner?.Invoke(builder.Services);
            host = builder.Build();
            _innerStopExpected = false;
            await host.StartAsync(ct).ConfigureAwait(false);
            return host;
        }
        catch
        {
            if (host is not null)
            {
                await StopAndDisposeAsync(host).ConfigureAwait(false);
            }

            throw;
        }
    }

    private void SetInner(IHost inner)
    {
        Volatile.Write(ref _inner, inner);

        // An inner host that stops by itself (a crashed background service) stops the process, as before.
        inner.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
        {
            if (!_innerStopExpected)
            {
                lifetime.StopApplication();
            }
        });
    }

    private async Task StopInnerAsync()
    {
        if (Interlocked.Exchange(ref _inner, null) is { } inner)
        {
            await StopAndDisposeAsync(inner).ConfigureAwait(false);
        }
    }

    private async Task StopAndDisposeAsync(IHost host)
    {
        _innerStopExpected = true;
        try
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The inner Edge host did not stop cleanly.");
        }

        // Disposing releases the singletons: the PLC driver's connection, the monitor's port, the database pool.
        try
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                host.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The inner Edge host did not dispose cleanly.");
        }
    }

    private static string Revision(IHost inner) => inner.Services.GetRequiredService<EdgeSettingsFile>().Revision;

    private static string MarkerPath(string settingsPath) => Path.ChangeExtension(settingsPath, ".pending");

    private bool TryReadMarker(string markerPath, out byte[]? previous)
    {
        previous = null;
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(markerPath));
            previous = marker?.Previous is { } encoded ? Convert.FromBase64String(encoded) : null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException)
        {
            // A marker cut short by the crash itself: nothing to restore from; start as usual.
            logger.LogWarning("Ignoring an unreadable apply marker {Path} ({Reason}).", markerPath, ex.Message);
            File.Delete(markerPath);
            return false;
        }
    }

    private static void WriteMarker(string markerPath, byte[]? previous) =>
        WriteAtomic(markerPath, JsonSerializer.SerializeToUtf8Bytes(new Marker(previous is null ? null : Convert.ToBase64String(previous))));

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/>, or deletes it when null.</summary>
    private static void RestoreFile(string path, byte[]? bytes)
    {
        if (bytes is null)
        {
            File.Delete(path);
        }
        else
        {
            WriteAtomic(path, bytes);
        }
    }

    /// <summary>A temporary file in the same directory, flushed, then renamed over the target.</summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record Marker(string? Previous);

    /// <summary>Inner hosts neither wait for nor signal the console or the SCM; the outer host does.</summary>
    private sealed class InnerHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
