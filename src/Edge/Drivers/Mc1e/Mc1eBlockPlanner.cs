namespace IndustrialIot.Edge.Drivers.Mc1e;

/// <summary>
/// One tag in a batch: its position in the caller's list and the words it occupies. A D word is one register; a bit
/// device word is 16 devices from a multiple of 16, as the adapter reads bit devices in word units.
/// </summary>
internal sealed record Mc1eTagSlot(int Index, ResolvedTag Tag, Mc1eAddress Address, int Words)
{
    public int FirstWord => Address.IsBit ? Address.Number >> 4 : Address.Number;

    public int End => FirstWord + Words;
}

/// <summary>One batch read: <see cref="Count"/> words from word <see cref="Head"/>, covering whole <see cref="Slots"/>.</summary>
internal sealed record Mc1eBlock(Mc1eDevice Device, int Head, int Count, IReadOnlyList<Mc1eTagSlot> Slots)
{
    /// <summary>The head device number of the request: a bit device word starts at 16 × its index.</summary>
    public int HeadDevice => Mc1eFrame.IsBitDevice(Device) ? Head * 16 : Head;
}

/// <summary>
/// Groups tags into batch reads within the adapter's limits (64 words from D, 32 words from a bit device). Gap words
/// are never read and a tag is never split across two reads.
/// </summary>
internal static class Mc1eBlockPlanner
{
    public static int MaxWords(Mc1eDevice device) => Mc1eFrame.IsBitDevice(device) ? Mc1eFrame.MaxReadBitWords : Mc1eFrame.MaxReadWords;

    public static IReadOnlyList<Mc1eBlock> Plan(IEnumerable<Mc1eTagSlot> slots)
    {
        var blocks = new List<Mc1eBlock>();
        foreach (var device in slots.GroupBy(s => s.Address.Device).OrderBy(g => g.Key))
        {
            var max = MaxWords(device.Key);
            var current = new List<Mc1eTagSlot>();
            int head = 0, end = 0;
            foreach (var slot in device.OrderBy(s => s.FirstWord))
            {
                if (current.Count == 0 || slot.FirstWord > end || Math.Max(end, slot.End) - head > max)
                {
                    Flush();
                    head = slot.FirstWord;
                    end = slot.End;
                }
                else
                {
                    end = Math.Max(end, slot.End);
                }

                current.Add(slot);
            }

            Flush();

            void Flush()
            {
                if (current.Count > 0)
                {
                    blocks.Add(new Mc1eBlock(device.Key, head, end - head, current.ToArray()));
                    current.Clear();
                }
            }
        }

        return blocks;
    }
}
