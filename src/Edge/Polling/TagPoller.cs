using System.Text.Json;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Monitoring;
using IndustrialIot.Shared.Common;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Polling;

/// <summary>
/// Reads every catalog tag once per interval and stores the cycle as one batch.
/// Cycle k is due at <c>start + k × interval</c>; a cycle that overruns skips the due times it missed
/// instead of catching up in a burst. After the PLC turns out to be unreachable the poller also skips
/// the next cycle, so the single driver connection is free for remote writes in between.
/// Every cycle is also published to <see cref="LiveTagSnapshot"/> for the local monitor page.
/// </summary>
internal sealed class TagPoller(
    IPlcDriver driver,
    PlcTagCatalog catalog,
    TelemetryStore store,
    LiveTagSnapshot snapshot,
    IOptions<PollOptions> options,
    TimeProvider clock,
    ILogger<TagPoller> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(options.Value.IntervalMs);
    private long _waitsArmed;

    /// <summary>How many times the loop has started waiting for its next due time (test hook).</summary>
    internal long WaitsArmed => Interlocked.Read(ref _waitsArmed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (catalog.Tags.Count == 0)
            {
                logger.LogInformation("Tag catalog is empty; nothing to poll.");
                return;
            }

            var start = clock.GetUtcNow();
            while (!stoppingToken.IsCancellationRequested)
            {
                var plcUnavailable = await RunCycleAsync(stoppingToken).ConfigureAwait(false);

                var now = clock.GetUtcNow();
                var nextDue = (long)Math.Floor((now - start) / _interval) + 1; // first due time strictly after now
                if (plcUnavailable)
                {
                    nextDue++; // leave the driver lock free for one whole interval
                }

                var delay = start + (nextDue * _interval) - now;

                // Create the timer first, then report "waiting": a test may move the clock as soon as it sees it.
                var wait = Task.Delay(delay, clock, stoppingToken);
                Interlocked.Increment(ref _waitsArmed);
                await wait.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    /// <summary>Reads all tags and stores them. Returns true when the PLC was unreachable.</summary>
    private async Task<bool> RunCycleAsync(CancellationToken ct)
    {
        var samples = new List<TelemetrySample>(catalog.Tags.Count);
        var errors = new List<string?>(catalog.Tags.Count);
        string? unavailableReason = null;
        var plcUnavailable = false;
        var unexpectedLogged = false;

        var results = await ReadAllAsync(ct).ConfigureAwait(false);

        for (var i = 0; i < catalog.Tags.Count; i++)
        {
            var tag = catalog.Tags[i];
            switch (results[i])
            {
                case { Error: null }:
                    var sample = ToSample(tag, results[i].Value);
                    samples.Add(sample);
                    errors.Add(sample.Quality == TelemetryQuality.Good ? null : "Value is not a finite number.");
                    break;

                case { Error: PlcUnavailableException } when plcUnavailable:
                    // Includes PlcBusyException. The driver skipped the other tags; they would only wait for the same timeout.
                    samples.Add(Bad(tag));
                    errors.Add($"Not read: {unavailableReason}");
                    break;

                case { Error: PlcUnavailableException ex }:
                    logger.LogWarning("PLC unavailable while reading {TagId}: {Message}", tag.TagId, ex.Message);
                    plcUnavailable = true;
                    unavailableReason = ex.Message;
                    samples.Add(Bad(tag));
                    errors.Add(ex.Message);
                    break;

                case { Error: PlcAccessDeniedException or PlcValueException } failed:
                    logger.LogWarning("Reading {TagId} failed: {Message}", tag.TagId, failed.Error.Message);
                    samples.Add(Bad(tag));
                    errors.Add(failed.Error.Message);
                    break;

                default:
                    if (!unexpectedLogged)
                    {
                        logger.LogError(results[i].Error, "Unexpected error while reading {TagId}.", tag.TagId);
                        unexpectedLogged = true;
                    }

                    samples.Add(Bad(tag));
                    errors.Add("Unexpected error; see the Edge log.");
                    break;
            }
        }

        // Before storing: the monitor shows what the PLC returned even when the disk is failing.
        snapshot.Publish(new PollCycle(
            clock.GetUtcNow(),
            plcUnavailable,
            [.. samples.Select((s, i) => new TagReading(s.TagId, s.Value, s.Quality, s.TimestampUtc, errors[i]))]));

        try
        {
            await store.AppendBatchAsync(samples, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A full or locked disk must not stop polling; the next cycle tries again.
            logger.LogError(ex, "Storing a poll cycle of {Count} samples failed.", samples.Count);
        }

        return plcUnavailable;
    }

    /// <summary>
    /// One batch read. A driver that throws or returns the wrong number of results must not stop the host:
    /// every tag then gets the same unexpected-error result, which the cycle logs once.
    /// </summary>
    private async Task<IReadOnlyList<PlcReadResult>> ReadAllAsync(CancellationToken ct)
    {
        Exception failure;
        try
        {
            var results = await driver.ReadAsync(catalog.Tags, ct).ConfigureAwait(false);
            if (results.Count == catalog.Tags.Count)
            {
                return results;
            }

            failure = new InvalidOperationException(
                $"The driver returned {results.Count} results for {catalog.Tags.Count} tags.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Wrapped so that even a PLC exception from the batch call itself is reported as unexpected.
            failure = new InvalidOperationException("The driver's batch read failed.", ex);
        }

        return [.. catalog.Tags.Select(_ => PlcReadResult.Fail(failure))];
    }

    private TelemetrySample ToSample(ResolvedTag tag, object? value)
    {
        var now = clock.GetUtcNow();

        // bool → true/false; integer types → JSON integers; float → shortest round-trip number.
        // Anything else (NaN/±Inf, an unexpected CLR type) is a bad sample, so one odd value cannot
        // invalidate the whole batch.
        JsonElement? json = value switch
        {
            bool b => JsonSerializer.SerializeToElement(b),
            byte or ushort or short or uint or int => JsonSerializer.SerializeToElement(value, value.GetType()),
            float real when float.IsFinite(real) => JsonSerializer.SerializeToElement(real),
            _ => null,
        };

        return json is null
            ? new TelemetrySample(tag.TagId, null, TelemetryQuality.Bad, now)
            : new TelemetrySample(tag.TagId, json, TelemetryQuality.Good, now);
    }

    private TelemetrySample Bad(ResolvedTag tag) => new(tag.TagId, null, TelemetryQuality.Bad, clock.GetUtcNow());
}
