namespace IndustrialIot.Edge.Drivers.Models;

/// <summary>Raised when an address string does not match the supported S7 grammar.</summary>
public sealed class PlcAddressException : PlcException
{
    public PlcAddressException(string? input, string reason)
        : base($"Invalid PLC address '{input ?? "<null>"}': {reason}")
    {
        Input = input;
    }

    /// <summary>The original, untrimmed input that failed to parse.</summary>
    public string? Input { get; }
}
