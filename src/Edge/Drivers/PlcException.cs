namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// Base type for every error raised by the PLC driver layer, so callers can catch
/// driver failures in one place without also catching unrelated exceptions.
/// </summary>
public abstract class PlcException : Exception
{
    protected PlcException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
