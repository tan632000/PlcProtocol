namespace IndustrialIot.Shared.Common;

/// <summary>Final outcome of a <c>DeviceCommand</c>, reported by the Edge in a <c>CommandResult</c>.</summary>
public enum CommandOutcome
{
    /// <summary>The PLC acknowledged the write.</summary>
    Applied,

    /// <summary>The Edge refused the command before any PLC I/O (see <c>reason</c>).</summary>
    Rejected,

    /// <summary>The command arrived after <c>expiresAtUtc</c>.</summary>
    Expired,

    /// <summary>The same <c>commandId</c> was already processed.</summary>
    Duplicate,

    /// <summary>Nothing was written (e.g. PLC unreachable before sending).</summary>
    Failed,

    /// <summary>The write may or may not have reached the PLC. Read the tag back; never auto-retry.</summary>
    Unknown,
}
