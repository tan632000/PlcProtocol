using System.Text.Json;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Slmp;
using IndustrialIot.Edge.Messaging;
using IndustrialIot.Edge.Polling;
using IndustrialIot.Edge.Settings;
using IndustrialIot.Shared.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Monitoring;

/// <summary>
/// Serves the local monitor page (<c>/</c>) and its data (<c>/api/status</c>) on <c>localhost</c> only.
/// Read-only by design: it shows the poller's last cycle, the MQTT link and the buffer backlog, never
/// talks to the PLC itself and offers no way to write. A port that is already taken is logged and the
/// Edge keeps running without the page.
/// </summary>
internal sealed class MonitorServer(
    IOptions<MonitorOptions> options,
    IOptions<EdgeOptions> edge,
    IOptions<PlcProtocolOptions> protocol,
    IOptions<SiemensS7Options> s7,
    IOptions<SlmpOptions> slmp,
    IOptions<MqttOptions> mqtt,
    IOptions<PollOptions> poll,
    PlcTagCatalog catalog,
    LiveTagSnapshot snapshot,
    TelemetryStore store,
    MqttConnection mqttConnection,
    TimeProvider clock,
    ILogger<MonitorServer> logger,
    EdgeSettingsFile settingsFile,
    EdgeRestarter? restarter = null,
    MonitorStartPolicy? startPolicy = null,
    SettingsService? settingsApi = null,
    PlcConnectionTester? connectionTester = null) : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> Page = new(LoadPage);
    private static readonly Lazy<string> SettingsPage = new(() => LoadPage("settings.html"));

    // DNS rebinding guard: a web page from elsewhere could resolve its own name to 127.0.0.1.
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "127.0.0.1", "[::1]", "::1",
    };

    private WebApplication? _app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        // At process start a taken port leaves the Edge running without the page. When settings are applied the old
        // host has only just released the port: retry, then fail the start so the supervisor rolls back.
        var policy = startPolicy ?? MonitorStartPolicy.ProcessStart;
        var retrying = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var app = BuildApp(settings.Port);
            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                _app = app;
                logger.LogInformation("Monitor page: http://localhost:{Port}/", settings.Port);
                return;
            }
            catch (IOException ex)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                if (retrying.Elapsed < policy.BindRetry)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (policy.FailWhenUnavailable)
                {
                    throw new InvalidOperationException($"Monitor port {settings.Port} is not available ({ex.Message}).", ex);
                }

                logger.LogError("Monitor page disabled: port {Port} is not available ({Reason}).", settings.Port, ex.Message);
                return;
            }
            catch
            {
                // Cancelled or failed otherwise: never leave a half-started server holding the port.
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private WebApplication BuildApp(int port)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));
        builder.Services.AddRoutingCore();

        var app = builder.Build();
        app.Use(GuardAsync);
        app.MapGet("/", () => Results.Content(Page.Value, "text/html; charset=utf-8"));
        app.MapGet("/api/status", async (CancellationToken ct) => Results.Json(await BuildStatusAsync(ct).ConfigureAwait(false), Json));
        if (settingsApi is not null)
        {
            app.MapGet("/settings", () => Results.Content(SettingsPage.Value, "text/html; charset=utf-8"));
            app.MapGet("/api/settings", (RequestDelegate)settingsApi.GetAsync);
            app.MapPost("/api/settings/validate", (RequestDelegate)(context => settingsApi.ValidateAsync(context, port)));
            app.MapPut("/api/settings", (RequestDelegate)(context => settingsApi.SaveAsync(context, port)));
        }

        if (connectionTester is not null)
        {
            app.MapPost("/api/settings/test-connection", (RequestDelegate)(context => connectionTester.TestAsync(context, port)));
        }

        return app;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is { } app)
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _app, null) is { } app)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task<MonitorStatus> BuildStatusAsync(CancellationToken ct)
    {
        var cycle = snapshot.Latest;
        var readings = cycle?.Readings.ToDictionary(r => r.TagId, StringComparer.Ordinal);

        var tags = catalog.Tags.Select(tag =>
        {
            TagReading? reading = null;
            readings?.TryGetValue(tag.TagId, out reading);
            return new TagStatus(
                tag.TagId,
                tag.Address.Canonical,
                tag.DataType.ToString(),
                tag.Writable,
                reading?.Value,
                reading is null ? null : QualityName(reading.Quality),
                reading?.TimestampUtc,
                reading?.Error);
        }).ToList();

        long? pending;
        try
        {
            pending = await store.CountAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pending = null; // The page shows "unknown"; the store logs its own failures.
        }

        var plcState = cycle switch
        {
            null => "waiting",
            { PlcUnavailable: true } => "unavailable",
            _ when cycle.Readings.Any(r => r.Quality != TelemetryQuality.Good) => "partial",
            _ => "ok",
        };

        // Only the selected protocol's options are validated and meaningful; read just those.
        PlcProtocolOptions.TryParse(protocol.Value.Protocol, out var selected);
        var plcStatus = selected == PlcProtocol.Slmp
            ? new PlcStatus(nameof(PlcProtocol.Slmp), slmp.Value.Host, slmp.Value.Port, null, null, slmp.Value.TimeoutMs, plcState)
            : new PlcStatus(nameof(PlcProtocol.S7), s7.Value.Host, s7.Value.Port, s7.Value.Rack, s7.Value.Slot, s7.Value.TimeoutMs, plcState);
        var m = mqtt.Value;
        return new MonitorStatus(
            edge.Value.EdgeId,
            clock.GetUtcNow(),
            poll.Value.IntervalMs,
            cycle?.CompletedUtc,
            cycle is null ? null : Math.Round(cycle.ReadDuration.TotalMilliseconds, 1),
            plcStatus,
            new MqttStatus(m.Host, m.Port, mqttConnection.IsConnected),
            pending,
            tags,
            SettingsApply());
    }

    private SettingsApplyStatus SettingsApply()
    {
        // Without a supervisor (tests, a plain AddEdgeAgent host) the running settings are simply applied.
        var status = restarter?.Status ?? new ApplyStatus(settingsFile.Revision, null, ApplyState.Applied, null);
        var state = status.State switch
        {
            ApplyState.Applying => "applying",
            ApplyState.RolledBack => "rolledBack",
            _ => "applied",
        };
        return new SettingsApplyStatus(status.Revision, status.ApplyId, state, status.Error);
    }

    private static Task GuardAsync(HttpContext context, Func<Task> next)
    {
        if (!AllowedHosts.Contains(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";
        return next();
    }

    private static string QualityName(TelemetryQuality quality) => quality switch
    {
        TelemetryQuality.Good => "good",
        TelemetryQuality.Bad => "bad",
        _ => "uncertain",
    };

    private static string LoadPage() => LoadPage("monitor.html");

    private static string LoadPage(string name)
    {
        using var stream = typeof(MonitorServer).Assembly.GetManifestResourceStream($"IndustrialIot.Edge.Monitoring.{name}")
            ?? throw new InvalidOperationException($"Embedded page {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed record MonitorStatus(
    string EdgeId,
    DateTimeOffset NowUtc,
    int PollIntervalMs,
    DateTimeOffset? LastCycleUtc,
    double? ReadMs,
    PlcStatus Plc,
    MqttStatus Mqtt,
    long? BufferPending,
    IReadOnlyList<TagStatus> Tags,
    SettingsApplyStatus SettingsApply);

/// <summary>
/// <see cref="Revision"/>: the settings the running agent was built from. <see cref="State"/>: applying, applied or
/// rolledBack for <see cref="ApplyId"/>; <see cref="Error"/> is a fixed message.
/// </summary>
internal sealed record SettingsApplyStatus(string Revision, string? ApplyId, string State, string? Error);

/// <summary>
/// <see cref="Protocol"/>: S7 or Slmp; <see cref="Rack"/>/<see cref="Slot"/> are null for Slmp.
/// <see cref="State"/>: waiting (no cycle yet), ok, partial (some tags bad) or unavailable.
/// </summary>
internal sealed record PlcStatus(string Protocol, string Host, int Port, int? Rack, int? Slot, int TimeoutMs, string State);

internal sealed record MqttStatus(string Host, int Port, bool Connected);

internal sealed record TagStatus(
    string TagId,
    string Address,
    string DataType,
    bool Writable,
    JsonElement? Value,
    string? Quality,
    DateTimeOffset? TimestampUtc,
    string? Error);
