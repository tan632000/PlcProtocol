namespace IndustrialIot.Edge.Drivers;

/// <summary>A value does not fit the tag's data type, range, or address size. Raised before any PLC I/O.</summary>
public sealed class PlcValueException : PlcException
{
    public PlcValueException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The PLC could not be reached and nothing was sent to it, so the operation is safe to retry.
/// </summary>
public class PlcUnavailableException : PlcException
{
    public PlcUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Waiting for the driver's single connection timed out. Like its base type, nothing was sent.
/// </summary>
public sealed class PlcBusyException : PlcUnavailableException
{
    public PlcBusyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The PLC answered and explicitly refused the request (e.g. PUT/GET disabled, optimized DB).</summary>
public sealed class PlcAccessDeniedException : PlcException
{
    public PlcAccessDeniedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A write request may have reached the PLC but no acknowledgement arrived, so the value may or may not
/// have been applied. Never retry automatically: repeating it can actuate equipment twice.
/// </summary>
public sealed class PlcWriteOutcomeUnknownException : PlcException
{
    public PlcWriteOutcomeUnknownException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
