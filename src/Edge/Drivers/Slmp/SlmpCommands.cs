namespace IndustrialIot.Edge.Drivers.Slmp;

/// <summary>
/// The only SLMP commands the driver can send: Device Read and Device Write. Remote operations, password commands and
/// every other command are deliberately absent, and nothing else belongs in this class (subcommands live with the
/// frame builder): a test checks its constants are exactly these two.
/// </summary>
internal static class SlmpCommands
{
    public const ushort DeviceRead = 0x0401;

    public const ushort DeviceWrite = 0x1401;
}
