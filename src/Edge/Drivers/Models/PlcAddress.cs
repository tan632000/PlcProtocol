namespace IndustrialIot.Edge.Drivers.Models;

/// <summary>
/// An immutable, validated S7 address. Create it only through <see cref="PlcAddressParser"/>
/// so every instance is guaranteed to be within the documented ranges.
/// </summary>
public sealed record PlcAddress : IPlcAddress
{
    internal PlcAddress(PlcArea area, int dbNumber, int byteOffset, int? bitOffset, int sizeBytes)
    {
        Area = area;
        DbNumber = dbNumber;
        ByteOffset = byteOffset;
        BitOffset = bitOffset;
        SizeBytes = sizeBytes;
        Canonical = BuildCanonical();
    }

    public PlcArea Area { get; }

    /// <summary>DB number for <see cref="PlcArea.DataBlock"/>; 0 for every other area.</summary>
    public int DbNumber { get; }

    public int ByteOffset { get; }

    /// <summary>Bit index 0..7 for bit addresses; <c>null</c> otherwise.</summary>
    public int? BitOffset { get; }

    /// <summary>1, 2 or 4 for byte/word/dword addresses; 0 for bit addresses.</summary>
    public int SizeBytes { get; }

    public bool IsBit => BitOffset.HasValue;

    /// <summary>Normalized spelling (uppercase, no leading zeros), e.g. <c>DB1.DBW2</c>.</summary>
    public string Canonical { get; }

    /// <summary>The input area is read-only by definition; every other S7 area can be written.</summary>
    public string? ReadOnlyReason => Area == PlcArea.Input ? "input area (I) tags cannot be writable" : null;

    public override string ToString() => Canonical;

    private string BuildCanonical()
    {
        if (Area == PlcArea.DataBlock)
        {
            return IsBit
                ? $"DB{DbNumber}.DBX{ByteOffset}.{BitOffset}"
                : $"DB{DbNumber}.DB{SizeLetter(SizeBytes)}{ByteOffset}";
        }

        var areaLetter = Area switch
        {
            PlcArea.Memory => 'M',
            PlcArea.Input => 'I',
            PlcArea.Output => 'Q',
            _ => throw new InvalidOperationException($"Unexpected area {Area}."),
        };

        return IsBit
            ? $"{areaLetter}{ByteOffset}.{BitOffset}"
            : $"{areaLetter}{SizeLetter(SizeBytes)}{ByteOffset}";
    }

    private static char SizeLetter(int sizeBytes) => sizeBytes switch
    {
        1 => 'B',
        2 => 'W',
        4 => 'D',
        _ => throw new InvalidOperationException($"Unexpected size {sizeBytes}."),
    };
}
