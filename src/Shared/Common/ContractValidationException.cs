namespace IndustrialIot.Shared.Common;

/// <summary>
/// Raised when a contract message is malformed: invalid JSON, a missing required field,
/// or a field that breaks its rule. For any message content, it is the only exception <c>ContractJson</c>
/// lets escape; caller mistakes such as passing a null contract to <c>Serialize</c> still throw argument exceptions.
/// </summary>
public sealed class ContractValidationException : Exception
{
    public ContractValidationException(string? field, string message, Exception? innerException = null)
        : base(field is null ? message : $"{field}: {message}", innerException)
    {
        Field = field;
    }

    /// <summary>JSON field (or JSON path) that failed, when known.</summary>
    public string? Field { get; }
}
