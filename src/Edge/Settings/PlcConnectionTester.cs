using System.Diagnostics;
using System.Text.Json;
using IndustrialIot.Edge.Configuration;
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
    PlcDeviceRegistry registry,
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
        ProposedPlc? proposed = null;
        DeviceRuntime? named = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("device", out _))
        {
            named = Named(root, errors);
        }
        else if (registry.Devices.Count > 1)
        {
            // Proposed settings without a device name come from the single-PLC page, which cannot say which PLC it means.
            await SettingsService.Reply(context, StatusCodes.Status409Conflict,
                new { error = "several-plcs", message = "This Edge has several PLCs; name the PLC to test." }).ConfigureAwait(false);
            return;
        }
        else
        {
            proposed = Proposed(root, errors);
        }

        if (proposed is null && named is null)
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
            reachable = named is not null
                ? await ReachableAsync(named, context.RequestAborted).ConfigureAwait(false)
                : await ReachableAsync(proposed!, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            limiter.Exit();
        }

        var result = reachable ? "ok" : "unreachable";
        if (named is not null)
        {
            logger.LogInformation("PLC connection test {Device} ({Model}) {Host}: {Result}.", named.Name, named.Device.Model, named.Device.Host, result);
        }
        else
        {
            logger.LogInformation("PLC connection test {Protocol} {Host}:{Port}: {Result}.", proposed!.Protocol, proposed.Host, proposed.Port, result);
        }
        await SettingsService.Reply(context, StatusCodes.Status200OK,
            new { result, elapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1) }).ConfigureAwait(false);
    }

    /// <summary>
    /// A PLC of this Edge: through its running driver, which keeps its single connection; a disabled one with a driver of
    /// its kind made for this test and disposed right after.
    /// </summary>
    private async Task<bool> ReachableAsync(DeviceRuntime device, CancellationToken ct)
    {
        if (device.Driver is { } running)
        {
            return await running.IsReachableAsync(ct).ConfigureAwait(false);
        }

        var driver = new PlcDriverFactory(loggerFactory).Create(device.Device);
        try
        {
            return await driver.IsReachableAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await ((IAsyncDisposable)driver).DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> ReachableAsync(ProposedPlc proposed, CancellationToken ct)
    {
        if (RunningDriver(proposed) is { } running)
        {
            return await running.IsReachableAsync(ct).ConfigureAwait(false);
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

    /// <summary>
    /// The running driver of the enabled device at <paramref name="proposed"/>'s protocol, host and port, which keeps
    /// that PLC's single connection; null when no device of this Edge reads that PLC.
    /// </summary>
    private IPlcDriver? RunningDriver(ProposedPlc proposed) => registry.Devices
        .Where(device => device.Driver is not null && device.Device.Driver != PlcDriverKind.Mc1E) // the page proposes S7 or SLMP only
        .FirstOrDefault(device => Endpoint(device.Device) is var (protocol, port)
            && protocol == proposed.Protocol
            && string.Equals(device.Device.Host, proposed.Host, StringComparison.OrdinalIgnoreCase)
            && port == proposed.Port)
        ?.Driver;

    private static (PlcProtocol Protocol, int Port) Endpoint(ResolvedDevice device) => device.Driver == PlcDriverKind.Slmp3E
        ? (PlcProtocol.Slmp, PlcDeviceRegistry.SlmpOptions(device).Port)
        : (PlcProtocol.S7, PlcDeviceRegistry.S7Options(device).Port);

    /// <summary>A PLC of this Edge by name: <c>{"password": ..., "device": "Line1"}</c>, tested with its own settings.</summary>
    private DeviceRuntime? Named(JsonElement root, List<SettingsError> errors)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("password" or "device"))
            {
                errors.Add(new SettingsError(property.Name, "Unknown key."));
            }
        }

        var name = root.GetProperty("device");
        var runtime = name.ValueKind == JsonValueKind.String
            ? registry.Devices.FirstOrDefault(d => d.Name.Equals(name.GetString(), StringComparison.OrdinalIgnoreCase))
            : null;
        if (runtime is null)
        {
            errors.Add(new SettingsError("device", "Unknown PLC: give the name of a PLC of this Edge."));
        }

        return errors.Count > 0 ? null : runtime;
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
