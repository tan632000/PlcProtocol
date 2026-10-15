namespace IndustrialIot.Shared.Common;

/// <summary>Result of <c>DeviceCommand.CheckFreshness</c>.</summary>
public enum CommandFreshness
{
    /// <summary>The command may be executed.</summary>
    Fresh,

    /// <summary>The Edge clock is at or after <c>expiresAtUtc</c>.</summary>
    Expired,

    /// <summary><c>issuedAtUtc</c> is too far in the future: Cloud and Edge clocks disagree.</summary>
    ClockSkew,
}
