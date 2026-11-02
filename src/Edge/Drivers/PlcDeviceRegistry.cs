using System.Diagnostics.CodeAnalysis;
using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Drivers.Models;
using IndustrialIot.Edge.Drivers.Mc1e;
using IndustrialIot.Edge.Drivers.Slmp;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// Builds one <see cref="DeviceRuntime"/> per configured device, each with a driver of its own from
/// <see cref="IPlcDriverFactory"/>, and maps every TagId to its device. Built when the host first resolves it (no PLC
/// I/O), so a host that stops right after starting still disposes every driver it created.
/// </summary>
public sealed class PlcDeviceRegistry : IAsyncDisposable
{
    private const int S7Port = 102;

    private readonly EdgeDeviceSet _set;
    private readonly IPlcDriverFactory _factory;
    private readonly IServiceProvider _services;
    private readonly ILogger<PlcDeviceRegistry> _logger;
    private readonly Dictionary<string, DeviceRuntime> _byTagId;

    public PlcDeviceRegistry(EdgeDeviceSet set, IPlcDriverFactory factory, IServiceProvider services, ILogger<PlcDeviceRegistry> logger)
    {
        _set = set;
        _factory = factory;
        _services = services;
        _logger = logger;
        Devices = Build();
        _byTagId = Devices
            .SelectMany(device => device.Catalog.Tags.Select(tag => (tag.TagId, device)))
            .ToDictionary(pair => pair.TagId, pair => pair.device, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every configured device in configuration order, disabled ones included (without a driver).</summary>
    public IReadOnlyList<DeviceRuntime> Devices { get; }

    public bool TryGetDevice(string tagId, [NotNullWhen(true)] out DeviceRuntime? device)
    {
        device = null;
        return tagId is not null && _byTagId.TryGetValue(tagId, out device);
    }

    public ValueTask DisposeAsync() => DisposeDriversAsync(Devices);

    private static async ValueTask DisposeDriversAsync(IEnumerable<DeviceRuntime> devices)
    {
        foreach (var driver in devices.Select(d => d.Driver).OfType<IPlcDriver>().Distinct())
        {
            if (driver is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (driver is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Checks every device of a multi-device configuration with the drivers' own option validators, the poll range
    /// and its tag catalog, so a bad device stops the start naming it.
    /// </summary>
    /// <exception cref="InvalidOperationException">At least one device is invalid.</exception>
    public static void ThrowIfInvalid(EdgeDeviceSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var errors = new List<string>();
        foreach (var device in set.Devices)
        {
            var prefix = $"Device '{device.Name}': ";
            if (!Enum.IsDefined(device.Driver))
            {
                errors.Add(NoDriverMessage(device));
                continue;
            }

            var options = device.Driver switch
            {
                PlcDriverKind.S7 => new SiemensS7OptionsValidator().Validate(null, S7Options(device)),
                PlcDriverKind.Slmp3E => new SlmpOptionsValidator().Validate(null, SlmpOptions(device)),
                _ => new Mc1eOptionsValidator().Validate(null, Mc1eOptions(device)),
            };
            if (options.Failed)
            {
                // The validators name the single-PLC section; here the device is the context.
                errors.AddRange(options.Failures.Select(f => prefix + f.Replace($"{PlcProtocolOptions.SectionName}:", "", StringComparison.Ordinal)));
            }

            if (device.PollIntervalMs is < 100 or > 3_600_000)
            {
                errors.Add($"{prefix}PollIntervalMs must be within 100..3600000 (was {device.PollIntervalMs}).");
            }

            try
            {
                _ = new PlcTagCatalog(new PlcTagCatalogOptions { Tags = [.. device.Tags] }, SyntaxFor(device.Driver));
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(prefix + ex.Message);
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{EdgeChannelsOptions.ChannelsKey} is invalid. {string.Join(" ", errors)}");
        }
    }

    internal static SiemensS7Options S7Options(ResolvedDevice device) => new()
    {
        Host = device.Host,
        Port = device.Port ?? S7Port,
        Rack = device.Rack ?? 0,
        Slot = device.Slot ?? 1,
        CpuType = device.CpuType ?? nameof(S7.Net.CpuType.S71200),
        TimeoutMs = device.TimeoutMs,
    };

    internal static SlmpOptions SlmpOptions(ResolvedDevice device) => new()
    {
        Host = device.Host,
        Port = device.Port ?? 0,
        TimeoutMs = device.TimeoutMs,
    };

    internal static Mc1eOptions Mc1eOptions(ResolvedDevice device) => new()
    {
        Host = device.Host,
        Port = device.Port ?? 0,
        TimeoutMs = device.TimeoutMs,
    };

    internal static string NoDriverMessage(ResolvedDevice device) =>
        $"Device '{device.Name}': the {device.Driver} driver ({device.Model}) is not available in this version.";

    private static IPlcAddressSyntax SyntaxFor(PlcDriverKind driver) => driver switch
    {
        PlcDriverKind.Slmp3E => new SlmpAddressSyntax(),
        PlcDriverKind.Mc1E => new Mc1eAddressSyntax(),
        _ => new S7AddressSyntax(),
    };

    private IReadOnlyList<DeviceRuntime> Build()
    {
        if (_set.MigratedFromV1)
        {
            // The single-PLC tag validator keeps its message ("Edge:Tags is invalid. ..."): reading the options runs it
            // before the catalog below is built from the same tags. The PLC options are validated at start, as before.
            _ = _services.GetRequiredService<IOptions<PlcTagCatalogOptions>>().Value;
        }

        var devices = new List<DeviceRuntime>(_set.Devices.Count);
        try
        {
            foreach (var device in _set.Devices)
            {
                var syntax = SyntaxFor(device.Driver);
                var catalog = new PlcTagCatalog(new PlcTagCatalogOptions { Tags = [.. device.Tags] }, syntax);
                if (SiemensS7Options.TryParseCpuType(device.CpuType, out var cpu) && cpu == S7.Net.CpuType.S71500)
                {
                    // The connection keeps the configured CPU type; only the model rules are those of the S7-1200.
                    _logger.LogWarning("S7-1500 is not supported; treated as S7-1200 for model rules (device {Device}).", device.Name);
                }

                devices.Add(new DeviceRuntime(device, device.Enabled ? _factory.Create(device) : null, syntax, catalog));
            }
        }
        catch
        {
            // A constructor that throws leaves no instance to dispose: release what it created.
            DisposeDriversAsync(devices).AsTask().GetAwaiter().GetResult();
            throw;
        }

        return devices;
    }
}
