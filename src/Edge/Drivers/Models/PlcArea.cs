namespace IndustrialIot.Edge.Drivers.Models;

/// <summary>S7 memory areas supported by the address grammar.</summary>
public enum PlcArea
{
    /// <summary>Data block (<c>DB</c>).</summary>
    DataBlock,

    /// <summary>Bit memory / merker (<c>M</c>).</summary>
    Memory,

    /// <summary>Process image input (<c>I</c>). Read-only by policy.</summary>
    Input,

    /// <summary>Process image output (<c>Q</c>).</summary>
    Output,
}
