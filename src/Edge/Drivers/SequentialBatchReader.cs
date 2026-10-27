namespace IndustrialIot.Edge.Drivers;

/// <summary>Outcome of reading one tag in a batch: a value, or the exception that stopped that tag.</summary>
public readonly record struct PlcReadResult(object? Value, Exception? Error)
{
    public static PlcReadResult Ok(object value) => new(value, null);

    public static PlcReadResult Fail(Exception error) => new(null, error);
}

/// <summary>
/// Reads tags one after another through a per-tag delegate. After the first <see cref="PlcUnavailableException"/>
/// no further I/O happens and that same exception is returned for every remaining tag, so an unreachable PLC
/// costs one timeout per cycle instead of one per tag. Other failures stay with their tag.
/// </summary>
public static class SequentialBatchReader
{
    public static async Task<IReadOnlyList<PlcReadResult>> ReadAsync(
        IReadOnlyList<ResolvedTag> tags,
        Func<ResolvedTag, CancellationToken, Task<object>> readOne,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(readOne);

        var results = new List<PlcReadResult>(tags.Count);
        PlcUnavailableException? unavailable = null;
        foreach (var tag in tags)
        {
            if (unavailable is not null)
            {
                results.Add(PlcReadResult.Fail(unavailable));
                continue;
            }

            try
            {
                results.Add(PlcReadResult.Ok(await readOne(tag, ct).ConfigureAwait(false)));
            }
            catch (PlcUnavailableException ex)
            {
                unavailable = ex;
                results.Add(PlcReadResult.Fail(ex));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(PlcReadResult.Fail(ex));
            }
        }

        return results;
    }
}
