namespace IndustrialIot.Edge.Drivers.Mc1e;

/// <summary>
/// The only MC 1E command codes the driver can send (FX3U-ENET-ADP User's Manual JY997D45801, section 7.4): batch read
/// and batch write. Remote RUN/STOP (13H/14H), random write and every other command are deliberately absent, and nothing
/// else belongs in this class: a test checks its constants are exactly these four.
/// </summary>
internal static class Mc1eCommands
{
    public const byte BatchReadBits = 0x00;

    public const byte BatchReadWords = 0x01;

    public const byte BatchWriteBits = 0x02;

    public const byte BatchWriteWords = 0x03;
}
