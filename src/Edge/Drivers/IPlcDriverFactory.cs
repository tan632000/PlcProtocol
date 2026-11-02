using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Drivers.Mc1e;
using IndustrialIot.Edge.Drivers.Slmp;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// Creates the driver of one device. Each device gets its own instance, so each owns its connection and request lock
/// and a PLC that hangs cannot hold up another. Tests register their own factory to read from fakes.
/// </summary>
public interface IPlcDriverFactory
{
    IPlcDriver Create(ResolvedDevice device);
}

/// <summary>
/// The real drivers: <c>S7</c> → <see cref="SiemensS7Driver"/>, <c>Slmp3E</c> → <see cref="MitsubishiSlmpDriver"/>,
/// <c>Mc1E</c> → <see cref="MitsubishiMc1eDriver"/>.
/// </summary>
internal sealed class PlcDriverFactory(ILoggerFactory loggers) : IPlcDriverFactory
{
    public IPlcDriver Create(ResolvedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Driver switch
        {
            PlcDriverKind.S7 => new SiemensS7Driver(
                Options.Create(PlcDeviceRegistry.S7Options(device)), loggers.CreateLogger<SiemensS7Driver>()),
            PlcDriverKind.Slmp3E => new MitsubishiSlmpDriver(Options.Create(PlcDeviceRegistry.SlmpOptions(device))),
            PlcDriverKind.Mc1E => new MitsubishiMc1eDriver(Options.Create(PlcDeviceRegistry.Mc1eOptions(device))),
            _ => throw new InvalidOperationException(PlcDeviceRegistry.NoDriverMessage(device)),
        };
    }
}
