namespace IndustrialIot.Edge.Drivers;

/// <summary>
/// Decides how many tags go into one S7 multi-variable read request. The limits are the ones S7netplus 0.20.0
/// enforces in <c>Plc.AssertPduSizeForRead</c> (S7.Net/PLC.cs) before sending, where it throws instead of
/// splitting: request <c>19 + 12·n ≤ PDU</c> and response <c>14 + 4·n + Σ even(length) ≤ PDU</c> (odd item lengths
/// are padded to even). The item cap matches Snap7's <c>MaxVars</c> (20). Re-check on a library upgrade.
/// </summary>
internal static class S7ReadPlanner
{
    internal const int MaxItems = 20;

    private const int RequestHeader = 19;
    private const int RequestPerItem = 12;
    private const int ResponseHeader = 14;
    private const int ResponsePerItem = 4;

    /// <summary>
    /// Number of items, taken greedily from <paramref name="start"/>, that fit in one request for
    /// <paramref name="pduSize"/>. Always at least one, so planning makes progress; a single item that does not
    /// fit is left to the library to reject.
    /// </summary>
    public static int Fit(IReadOnlyList<int> byteLengths, int start, int pduSize)
    {
        ArgumentNullException.ThrowIfNull(byteLengths);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(start, byteLengths.Count);

        var count = 1;
        var responseData = Even(byteLengths[start]);
        while (count < MaxItems && start + count < byteLengths.Count)
        {
            var next = count + 1;
            var nextData = responseData + Even(byteLengths[start + count]);
            if (RequestHeader + (RequestPerItem * next) > pduSize
                || ResponseHeader + (ResponsePerItem * next) + nextData > pduSize)
            {
                break;
            }

            count = next;
            responseData = nextData;
        }

        return count;
    }

    private static int Even(int length) => length + (length & 1);
}
