using System.Buffers.Binary;

namespace IndustrialIot.Edge.Drivers.Slmp;

/// <summary>
/// One tag in a batch: its position in the caller's list and the words it occupies. Words are counted per device:
/// a D word is one register, a bit device word is 16 devices aligned to a multiple of 16.
/// </summary>
internal sealed record SlmpTagSlot(int Index, ResolvedTag Tag, SlmpAddress Address, int Words)
{
    public int FirstWord => Address.IsBit ? Address.Number >> 4 : Address.Number;

    public int End => FirstWord + Words;
}

/// <summary>One request: <see cref="Count"/> words from word <see cref="Head"/>, covering whole <see cref="Slots"/>.</summary>
internal sealed record SlmpBlock(SlmpDevice Device, int Head, int Count, IReadOnlyList<SlmpTagSlot> Slots)
{
    public (SlmpDevice Device, int Head, int Count) Key => (Device, Head, Count);
}

/// <summary>Groups tags into word reads and decodes values from the words.</summary>
internal static class SlmpBlockPlanner
{
    /// <summary>Most words one device read may request.</summary>
    public const int MaxBlockWords = 960;

    /// <summary>Device number of word <paramref name="word"/>: a bit device word starts at a multiple of 16.</summary>
    public static int HeadDevice(SlmpDevice device, int word) => device == SlmpDevice.D ? word : word * 16;

    public static int WordCount(PlcDataType type) => type switch
    {
        PlcDataType.Int or PlcDataType.Word => 1,
        PlcDataType.DInt or PlcDataType.DWord or PlcDataType.Real => 2,
        _ => throw new PlcValueException($"{type} is not an SLMP word type."),
    };

    /// <summary>
    /// Merges tags of one device whose words touch or overlap into one read, and starts a new read at a gap
    /// (gap words are never read) or when the next tag would push the read past <see cref="MaxBlockWords"/>.
    /// A tag is never split across two reads.
    /// </summary>
    public static IReadOnlyList<SlmpBlock> Plan(IEnumerable<SlmpTagSlot> slots)
    {
        var blocks = new List<SlmpBlock>();
        foreach (var device in slots.GroupBy(s => s.Address.Device).OrderBy(g => g.Key))
        {
            var current = new List<SlmpTagSlot>();
            int head = 0, end = 0;
            foreach (var slot in device.OrderBy(s => s.FirstWord))
            {
                var startsNewBlock = current.Count == 0
                    || slot.FirstWord > end
                    || Math.Max(end, slot.End) - head > MaxBlockWords;
                if (startsNewBlock)
                {
                    Flush(device.Key);
                    head = slot.FirstWord;
                    end = slot.End;
                }
                else
                {
                    end = Math.Max(end, slot.End);
                }

                current.Add(slot);
            }

            Flush(device.Key);

            void Flush(SlmpDevice key)
            {
                if (current.Count > 0)
                {
                    blocks.Add(new SlmpBlock(key, head, end - head, current.ToArray()));
                    current.Clear();
                }
            }
        }

        return blocks;
    }

    /// <summary>Little-endian words, low word first, as the CLR type of <paramref name="type"/>.</summary>
    public static object Decode(PlcDataType type, ReadOnlySpan<ushort> words)
    {
        var wordCount = WordCount(type);
        var combined = wordCount == 2 ? words[0] | ((uint)words[1] << 16) : 0u;

        return type switch
        {
            PlcDataType.Int => (object)unchecked((short)words[0]),
            PlcDataType.Word => words[0],
            PlcDataType.DInt => unchecked((int)combined),
            PlcDataType.DWord => combined,
            _ => BitConverter.UInt32BitsToSingle(combined),
        };
    }

    /// <summary>Bit <c>number &amp; 0xF</c> of the word holding it (byte 0 = bits 0-7, byte 1 = bits 8-15).</summary>
    public static bool DecodeBit(int number, ushort word) => ((word >> (number & 0xF)) & 1) == 1;

    /// <summary>Reads the little-endian 16-bit words of a reply's data part.</summary>
    public static ushort[] ToWords(ReadOnlySpan<byte> data)
    {
        var words = new ushort[data.Length / 2];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(2 * i)..]);
        }

        return words;
    }
}
