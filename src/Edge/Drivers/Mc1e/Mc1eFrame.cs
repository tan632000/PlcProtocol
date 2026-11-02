using System.Buffers.Binary;

namespace IndustrialIot.Edge.Drivers.Mc1e;

/// <summary>
/// MC 1E device codes, as the value the manual lists (D = 4420H, sent low byte first). The closed set of devices the
/// driver can name.
/// </summary>
public enum Mc1eDevice : ushort
{
    D = 0x4420,
    M = 0x4D20,
    X = 0x5820,
    Y = 0x5920,
}

/// <summary>
/// The A-compatible 1E binary frame of the FX3U Ethernet adapter (FX3U-ENET-ADP User's Manual JY997D45801, sections
/// 7.3.3 and 7.5): command, PC number FFH, monitoring timer, head device number (4 bytes), device code (2 bytes),
/// number of points (1 byte) and a 00H, all low byte first. The reply starts with the command + 80H and a complete
/// code; 5BH is followed by an abnormal code and 00H.
/// </summary>
internal static class Mc1eFrame
{
    public const int RequestLength = 12;

    public const int ReplyHeaderLength = 2;

    /// <summary>Words one batch read may request from D (word devices): 64 points.</summary>
    public const int MaxReadWords = 64;

    /// <summary>Words one batch read may request from M, X or Y (bit devices in 16-point units): 32 words.</summary>
    public const int MaxReadBitWords = 32;

    /// <summary>Words one batch write may send to D: 64 points.</summary>
    public const int MaxWriteWords = 64;

    /// <summary>Points one batch write in bit units may send: 160.</summary>
    public const int MaxWriteBits = 160;

    /// <summary>The complete code that is followed by an abnormal code and 00H.</summary>
    public const byte PlcCannotProcess = 0x5B;

    private const byte PcNumber = 0xFF;

    private const byte ResponseFlag = 0x80;

    /// <summary>
    /// Batch read in word units (command 01H) of <paramref name="points"/> words from device number
    /// <paramref name="head"/>; a bit device's head is a multiple of 16 and each point is 16 devices.
    /// </summary>
    public static byte[] BatchReadWords(Mc1eDevice device, int head, int points, ushort monitoringTimer)
    {
        if (!Enum.IsDefined(device))
        {
            throw new ArgumentOutOfRangeException(nameof(device));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(head);
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(points, IsBitDevice(device) ? MaxReadBitWords : MaxReadWords);
        if (IsBitDevice(device) && head % 16 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(head), "A bit device read in word units starts at a multiple of 16.");
        }

        var frame = new byte[RequestLength];
        frame[0] = Mc1eCommands.BatchReadWords;
        frame[1] = PcNumber;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), monitoringTimer);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), head);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)device);
        frame[10] = (byte)points;
        frame[11] = 0x00;
        return frame;
    }

    /// <summary>Batch write in word units (command 03H) of <paramref name="words"/> to D registers from <paramref name="head"/>.</summary>
    public static byte[] BatchWriteWords(Mc1eDevice device, int head, IReadOnlyList<ushort> words, ushort monitoringTimer)
    {
        if (device != Mc1eDevice.D)
        {
            throw new ArgumentOutOfRangeException(nameof(device), "Word-unit writes go to D registers only.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(head);
        ArgumentOutOfRangeException.ThrowIfLessThan(words.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(words.Count, MaxWriteWords);

        var frame = Header(Mc1eCommands.BatchWriteWords, device, head, words.Count, monitoringTimer, 2 * words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(RequestLength + (2 * i)), words[i]);
        }

        return frame;
    }

    /// <summary>
    /// Batch write in bit units (command 02H) to M or Y from <paramref name="head"/>: four bits per point, the first point
    /// in the high nibble, a 0H dummy after an odd count. X is never written.
    /// </summary>
    public static byte[] BatchWriteBits(Mc1eDevice device, int head, IReadOnlyList<bool> points, ushort monitoringTimer)
    {
        if (device is not (Mc1eDevice.M or Mc1eDevice.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(device), "Bit-unit writes go to M or Y only.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(head);
        ArgumentOutOfRangeException.ThrowIfLessThan(points.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(points.Count, MaxWriteBits);

        var frame = Header(Mc1eCommands.BatchWriteBits, device, head, points.Count, monitoringTimer, (points.Count + 1) / 2);
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i])
            {
                frame[RequestLength + (i / 2)] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
            }
        }

        return frame;
    }

    private static byte[] Header(byte command, Mc1eDevice device, int head, int points, ushort monitoringTimer, int dataLength)
    {
        var frame = new byte[RequestLength + dataLength];
        frame[0] = command;
        frame[1] = PcNumber;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), monitoringTimer);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), head);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)device);
        frame[10] = (byte)points;
        frame[11] = 0x00;
        return frame;
    }

    /// <summary>The first reply byte for <paramref name="command"/>: the command with the response flag.</summary>
    public static byte ReplySubheader(byte command) => (byte)(command | ResponseFlag);

    public static bool IsBitDevice(Mc1eDevice device) => device != Mc1eDevice.D;

    /// <summary>
    /// Wait of the adapter for the PLC, in 250 ms units, kept 500 ms below the client budget so the adapter answers
    /// with its own timeout code (60H) before the client gives up.
    /// </summary>
    public static ushort MonitoringTimer(int timeoutMs) => (ushort)Math.Max(1, (timeoutMs - 500) / 250);
}
