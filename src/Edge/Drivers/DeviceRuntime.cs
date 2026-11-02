using IndustrialIot.Edge.Configuration;
using IndustrialIot.Edge.Drivers.Models;

namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// One PLC as the Edge runs it: its settings, its own driver (null when the device is disabled), its address syntax
/// and the catalog of its tags under their published TagIds.
/// </summary>
public sealed class DeviceRuntime(ResolvedDevice device, IPlcDriver? driver, IPlcAddressSyntax syntax, PlcTagCatalog catalog)
{
    public ResolvedDevice Device { get; } = device;

    public string Name => Device.Name;

    /// <summary>Null when the device is disabled: it is neither connected nor read.</summary>
    public IPlcDriver? Driver { get; } = driver;

    public IPlcAddressSyntax Syntax { get; } = syntax;

    public PlcTagCatalog Catalog { get; } = catalog;
}

/// <summary>Implemented by drivers that can say how many requests their last batch read took.</summary>
public interface IPlcReadStatistics
{
    /// <summary>Requests sent by the last completed batch read; -1 before the first one.</summary>
    int LastBatchRequests { get; }
}
