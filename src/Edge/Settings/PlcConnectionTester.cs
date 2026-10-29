using System.Diagnostics;
using System.Text.Json;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Drivers.Slmp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Settings;

public enum PlcTestAdmission
{
    Admitted,

    /// <summary>Another test is running.</summary>
    Busy,

    /// <summary>10 tests in the last minute.</summary>
    RateLimited,
}

/// <summary>
/// One connection test at a time, at most 10 a minute. Shared by every inner host (registered by the supervisor), so
/// a settings apply does not reset it.
/// </summary>
public sealed class PlcTestLimiter(TimeProvider clock)
{
    public const int PerMinute = 10;

    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _starts = new();
    private bool _running;

    public PlcTestAdmission TryEnter()
    {
        lock (_gate)
        {
            if (_running)
            {
                return PlcTestAdmission.Busy;
            }

            var now = clock.GetUtcNow();
            while (_starts.Count > 0 && now - _starts.Peek() >= TimeSpan.FromMinutes(1))
            {
                _starts.Dequeue();
            }

            if (_starts.Count >= PerMinute)
            {
                return PlcTestAdmission.RateLimited;
            }

            _starts.Enqueue(now);
            _running = true;
            return PlcTestAdmission.Admitted;
        }
    }

    public void Exit()
    {
        lock (_gate)
        {
            _running = false;
        }
    }
}

/// <summary>
/// <c>POST /api/settings/test-connection {password, plc}</c>: is the proposed PLC reachable, without saving anything?
/// The answer is only <c>ok</c> or <c>unreachable</c> (the drivers report a bool, and a finer answer would help to map
/// the OT network). The PLC already in use (same protocol, host and port) is asked through the running driver, which
/// keeps its single connection; any other is tried with a separate driver that is disposed right after. Checked in
/// this order: request guard, shape and the drivers' option validators, password (the same lockout as saves), one
/// test at a time and 10 a minute. Every test is logged.
/// </summary>
public sealed class PlcConnectionTester(
    SettingsPassword password,
    EdgeSettingsFile file,
    PlcTestLimiter limiter,
    IPlcDriver runningDriver,
    IOptions<PlcProtocolOptions> runningProtocol,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    ILogger<PlcConnectionTester> logger)
{
    private static readonly HashSet<string> PlcKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Protocol", "Host", "Port", "Rack", "Slot", "CpuType", "TimeoutMs",
    };

    public async Task TestAsync(HttpContext context, int monitorPort)
    {
        using var body = await SettingsService.ReadGuardedAsync(context, monitorPort).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        var root = body.RootElement;
        var errors = new List<SettingsError>();
        var proposed = Proposed(root, errors);
        if (proposed is null)
        {
            await SettingsService.Reply(context, StatusCodes.Status400BadRequest, new { ok = false, errors }).ConfigureAwait(false);
            return;
        }

        if (!await SettingsService.PasswordAcceptedAsync(context, root, password, file).ConfigureAwait(false))
        {
            return;
        }

        switch (limiter.TryEnter())
        {
            case PlcTestAdmission.Busy:
                await SettingsService.Reply(context, StatusCodes.Status409Conflict, new { error = "test-running" }).ConfigureAwait(false);
                return;
            case PlcTestAdmission.RateLimited:
                await SettingsService.Reply(context, StatusCodes.Status429TooManyRequests, new { error = "rate-limited" }).ConfigureAwait(false);
                return;
        }

        bool reachable;
        var clock = Stopwatch.StartNew();
        try
        {
            reachable = await ReachableAsync(proposed, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            limiter.Exit();
        }

        var result = reachable ? "ok" : "unreachable";
        logger.LogInformation("PLC connection test {Protocol} {Host}:{Port}: {Result}.", proposed.Protocol, proposed.Host, proposed.Port, result);
        await SettingsService.Reply(context, StatusCodes.Status200OK,
            new { result, elapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1) }).ConfigureAwait(false);
    }

    private async Task<bool> ReachableAsync(ProposedPlc proposed, CancellationToken ct)
    {
        if (IsRunningPlc(proposed))
        {
            return await runningDriver.IsReachableAsync(ct).ConfigureAwait(false);
        }

        IPlcDriver driver = proposed.Protocol == PlcProtocol.Slmp
            ? new MitsubishiSlmpDriver(Options.Create(proposed.Slmp))
            : new SiemensS7Driver(Options.Create(proposed.S7), loggerFactory.CreateLogger<SiemensS7Driver>());
        try
        {
            return await driver.IsReachableAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await ((IAsyncDisposable)driver).DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool IsRunningPlc(ProposedPlc proposed)
    {
        PlcProtocolOptions.TryParse(runningProtocol.Value.Protocol, out var running);
        if (running != proposed.Protocol)
        {
            return false;
        }

        var (host, port) = running == PlcProtocol.Slmp
            ? (services.GetRequiredService<IOptions<SlmpOptions>>().Value.Host, services.GetRequiredService<IOptions<SlmpOptions>>().Value.Port)
            : (services.GetRequiredService<IOptions<SiemensS7Options>>().Value.Host, services.GetRequiredService<IOptions<SiemensS7Options>>().Value.Port);
        return string.Equals(host, proposed.Host, StringComparison.OrdinalIgnoreCase) && port == proposed.Port;
    }

    /// <summary>Shape, binding and the drivers' own option validators; errors name key paths only.</summary>
    private static ProposedPlc? Proposed(JsonElement root, List<SettingsError> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SettingsError("", "The body must be a JSON object."));
            return null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("password" or "plc"))
            {
                errors.Add(new SettingsError(property.Name, "Unknown key."));
            }
        }

        if (!root.TryGetProperty("plc", out var plc) || plc.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SettingsError("plc", "Required, as an object like Edge:Plc."));
            return null;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in plc.EnumerateObject())
        {
            var path = $"{PlcProtocolOptions.SectionName}:{property.Name}";
            if (!PlcKeys.Contains(property.Name))
            {
                errors.Add(new SettingsError(path, "Unknown key."));
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                errors.Add(new SettingsError(path, "Must be a single value."));
            }
            else
            {
                values[path] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
            }
        }

        if (errors.Count > 0)
        {
            return null;
        }

        var section = new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(PlcProtocolOptions.SectionName);
        if (!PlcProtocolOptions.TryParse(section[nameof(PlcProtocolOptions.Protocol)], out var protocol))
        {
            errors.Add(new SettingsError(PlcProtocolOptions.KeyName, SettingsService.FixedMessage(PlcProtocolOptions.KeyName)));
            return null;
        }

        SiemensS7Options s7;
        SlmpOptions slmp;
        try
        {
            s7 = section.Get<SiemensS7Options>() ?? new SiemensS7Options();
            slmp = section.Get<SlmpOptions>() ?? new SlmpOptions();
        }
        catch (InvalidOperationException)
        {
            errors.Add(new SettingsError(PlcProtocolOptions.SectionName, "A value does not have the expected type."));
            return null;
        }

        var verdict = protocol == PlcProtocol.Slmp
            ? new SlmpOptionsValidator().Validate(null, slmp)
            : new SiemensS7OptionsValidator().Validate(null, s7);
        if (verdict.Failed)
        {
            foreach (var failure in verdict.Failures)
            {
                var path = SettingsService.PathOf(failure);
                errors.Add(new SettingsError(path, SettingsService.FixedMessage(path)));
            }

            return null;
        }

        return protocol == PlcProtocol.Slmp
            ? new ProposedPlc(protocol, slmp.Host, slmp.Port, s7, slmp)
            : new ProposedPlc(protocol, s7.Host, s7.Port, s7, slmp);
    }

    private sealed record ProposedPlc(PlcProtocol Protocol, string Host, int Port, SiemensS7Options S7, SlmpOptions Slmp);
}
