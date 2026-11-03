using System.Diagnostics;
using System.Text.Json;
using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Drivers;
using Microsoft.AspNetCore.Http;

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
/// <c>POST /api/settings/test-connection</c>: is a PLC reachable, without saving anything? Either a PLC of this Edge by
/// name, <c>{password, device}</c>, or an unsaved one, <c>{password, proposed: {driver, model, host, port, rack, slot,
/// timeoutMs}}</c> with the model's defaults for what is left out. The answer is only <c>ok</c> or <c>unreachable</c>
/// (the drivers report a bool, and a finer answer would help to map the OT network). A running device with exactly that
/// connection answers through its own driver, which keeps its single connection; the same host and port with any other
/// setting is refused (<c>endpoint-in-use</c>), so a test never opens a second connection to a PLC the Edge reads. Any
/// other PLC is tried with a driver made for the test and disposed right after. Checked in this order: request guard,
/// shape and the Edge's own device rules, password (the same lockout as saves), endpoint in use, one test at a time and
/// 10 a minute. Every test is logged.
/// </summary>
public sealed class PlcConnectionTester(
    SettingsPassword password,
    EdgeSettingsFile file,
    PlcTestLimiter limiter,
    PlcDeviceRegistry registry,
    ILoggerFactory loggerFactory,
    ILogger<PlcConnectionTester> logger)
{
    private const string ProposedKey = "proposed";

    private static readonly string[] ProposedKeys = ["driver", "model", "host", "port", "rack", "slot", "timeoutMs"];

    public async Task TestAsync(HttpContext context, int monitorPort)
    {
        using var body = await SettingsService.ReadGuardedAsync(context, monitorPort).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        var root = body.RootElement;
        var errors = new List<SettingsError>();
        ResolvedDevice? proposed = null;
        DeviceRuntime? named = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("device", out _))
        {
            named = Named(root, errors);
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

        IPlcDriver? running = null;
        if (proposed is not null)
        {
            var (inUse, sameSettings) = RunningAt(proposed);
            if (inUse is not null && !sameSettings)
            {
                await SettingsService.Reply(context, StatusCodes.Status409Conflict, new
                {
                    error = "endpoint-in-use",
                    message = "A PLC of this Edge uses this host and port with other settings; save first, then test it by name.",
                }).ConfigureAwait(false);
                return;
            }

            running = inUse;
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
                ? await ReachableAsync(named.Driver, named.Device, context.RequestAborted).ConfigureAwait(false)
                : await ReachableAsync(running, proposed!, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            limiter.Exit();
        }

        var result = reachable ? "ok" : "unreachable";
        var device = named?.Device ?? proposed!;
        logger.LogInformation(
            "PLC connection test {Device} ({Model}) {Host}:{Port}: {Result}.",
            named?.Name ?? "(unsaved)", device.Model, device.Host, Port(device), result);
        await SettingsService.Reply(context, StatusCodes.Status200OK,
            new { result, elapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1) }).ConfigureAwait(false);
    }

    /// <summary>
    /// Through <paramref name="running"/> when there is one, which keeps that PLC's single connection; otherwise with a
    /// driver of the device's kind made for this test and disposed right after.
    /// </summary>
    private async Task<bool> ReachableAsync(IPlcDriver? running, ResolvedDevice device, CancellationToken ct)
    {
        if (running is not null)
        {
            return await running.IsReachableAsync(ct).ConfigureAwait(false);
        }

        var driver = new PlcDriverFactory(loggerFactory).Create(device);
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
    /// The running driver of the enabled device at <paramref name="proposed"/>'s host and port (trimmed, case-insensitive),
    /// and whether that device has the same connection settings (driver; rack and slot for S7). Null when no running
    /// device reads that endpoint.
    /// </summary>
    private (IPlcDriver? Driver, bool SameSettings) RunningAt(ResolvedDevice proposed)
    {
        var device = registry.Devices.FirstOrDefault(d => d.Driver is not null
            && string.Equals(d.Device.Host.Trim(), proposed.Host, StringComparison.OrdinalIgnoreCase)
            && Port(d.Device) == Port(proposed));
        if (device is null)
        {
            return (null, false);
        }

        var same = device.Device.Driver == proposed.Driver
            && (proposed.Driver != PlcDriverKind.S7
                || (PlcDeviceRegistry.S7Options(device.Device) is var mine && PlcDeviceRegistry.S7Options(proposed) is var theirs
                    && mine.Rack == theirs.Rack && mine.Slot == theirs.Slot));
        return (device.Driver, same);
    }

    private static int Port(ResolvedDevice device) => device.Driver switch
    {
        PlcDriverKind.S7 => PlcDeviceRegistry.S7Options(device).Port,
        PlcDriverKind.Slmp3E => PlcDeviceRegistry.SlmpOptions(device).Port,
        _ => PlcDeviceRegistry.Mc1eOptions(device).Port,
    };

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

    /// <summary>
    /// An unsaved PLC, checked by the rules a saved one meets: the channel validator (driver, model of that driver, port
    /// for the model, host) and the device's own checks (driver options). Errors name <c>proposed:key</c> only.
    /// </summary>
    private static ResolvedDevice? Proposed(JsonElement root, List<SettingsError> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SettingsError("", "The body must be a JSON object."));
            return null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("password" or ProposedKey))
            {
                errors.Add(new SettingsError(property.Name, "Unknown key."));
            }
        }

        if (!root.TryGetProperty(ProposedKey, out var given) || given.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new SettingsError(ProposedKey, "Required, as an object with driver, model and host."));
            return null;
        }

        // One channel with one device, keyed as a saved one is, so the same validators judge it.
        const string DeviceKey = $"{EdgeChannelsOptions.ChannelsKey}:0:Devices:0";
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{EdgeChannelsOptions.ChannelsKey}:0:Name"] = "Test",
            [$"{DeviceKey}:Name"] = "Test",
        };
        foreach (var property in given.EnumerateObject())
        {
            var key = ProposedKeys.FirstOrDefault(k => k.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
            if (key is null)
            {
                errors.Add(new SettingsError($"{ProposedKey}:{property.Name}", "Unknown key."));
            }
            else if (property.Value.ValueKind == JsonValueKind.Null)
            {
                // An empty field on the page: left out, so the model's default applies.
                continue;
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                errors.Add(new SettingsError($"{ProposedKey}:{key}", "Must be a single value."));
            }
            else
            {
                var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
                values[key == "driver" ? $"{EdgeChannelsOptions.ChannelsKey}:0:Driver" : $"{DeviceKey}:{key}"] = value;
            }
        }

        if (errors.Count > 0)
        {
            return null;
        }

        EdgeChannelsOptions options;
        try
        {
            options = new ConfigurationBuilder().AddInMemoryCollection(values).Build()
                .GetSection(EdgeChannelsOptions.SectionName).Get<EdgeChannelsOptions>() ?? new EdgeChannelsOptions();
        }
        catch (InvalidOperationException)
        {
            errors.Add(new SettingsError(ProposedKey, "A value does not have the expected type."));
            return null;
        }

        foreach (var error in EdgeChannelsValidator.Validate(options))
        {
            AddError(errors, SettingsService.PathOf(error).Split(':')[^1]);
        }

        if (errors.Count > 0)
        {
            return null;
        }

        var channel = options.Channels[0];
        EdgeChannelsValidator.TryParseDriver(channel.Driver, out var driver);
        var device = EdgeDeviceResolver.ToDevice(channel, driver, channel.Devices[0]);
        foreach (var problem in PlcDeviceRegistry.Check(device))
        {
            AddError(errors, problem.Field);
        }

        return errors.Count > 0 ? null : device;
    }

    /// <summary>One error per field, as <c>proposed:field</c> with the field named as the page sends it.</summary>
    private static void AddError(List<SettingsError> errors, string field)
    {
        var key = field == "Driver" ? "driver" : ProposedKeys.FirstOrDefault(k => k.Equals(field, StringComparison.OrdinalIgnoreCase)) ?? field;
        var path = $"{ProposedKey}:{key}";
        if (errors.All(e => e.Path != path))
        {
            errors.Add(new SettingsError(path, SettingsService.FixedMessage($"{EdgeChannelsOptions.ChannelsKey}:0:Devices:0:{field}")));
        }
    }
}
