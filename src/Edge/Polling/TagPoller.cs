using System.Text.Json;
using IndustrialIot.Edge.Buffering;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Monitoring;
using IndustrialIot.Shared.Common;

namespace IndustrialIot.Edge.Polling;

/// <summary>
/// Reads every enabled device in a loop of its own: each device's tags once per its interval, stored as one batch.
/// Cycle k of a device is due at <c>start + k × interval</c>; a cycle that overruns skips the due times it missed
/// instead of catching up in a burst. After a PLC turns out to be unreachable its loop also skips the next cycle, so
/// that device's driver connection is free for remote writes in between. Devices never wait for each other.
/// Every cycle is also published to <see cref="LiveTagSnapshot"/> under the device name for the local monitor page.
/// </summary>
internal sealed class TagPoller(
    PlcDeviceRegistry registry,
    TelemetryStore store,
    LiveTagSnapshot snapshot,
    TimeProvider clock,
    ILogger<TagPoller> logger) : BackgroundService
{
    private long _waitsArmed;

    /// <summary>How many times any device loop has started waiting for its next due time (test hook).</summary>
    internal long WaitsArmed => Interlocked.Read(ref _waitsArmed);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var polled = registry.Devices.Where(device => device.Driver is not null && device.Catalog.Tags.Count > 0).ToList();
        if (polled.Count == 0)
        {
            logger.LogInformation("Tag catalog is empty; nothing to poll.");
            return Task.CompletedTask;
        }

        return Task.WhenAll(polled.Select(device => PollDeviceAsync(device, stoppingToken)));
    }

    private async Task PollDeviceAsync(DeviceRuntime device, CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(device.Device.PollIntervalMs);
        try
        {
            var start = clock.GetUtcNow();
            while (!stoppingToken.IsCancellationRequested)
            {
                var plcUnavailable = await RunCycleAsync(device, stoppingToken).ConfigureAwait(false);

                var now = clock.GetUtcNow();
                var nextDue = (long)Math.Floor((now - start) / interval) + 1; // first due time strictly after now
                if (plcUnavailable)
                {
                    nextDue++; // leave the driver lock free for one whole interval
                }

                var delay = start + (nextDue * interval) - now;

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
        catch (Exception ex)
        {
            // The other devices keep polling; without this line the stop would only surface when the host stops.
            logger.LogCritical(ex, "Polling of PLC {Device} stopped unexpectedly.", device.Name);
            throw;
        }
    }

    /// <summary>Reads all tags of one device and stores them. Returns true when the PLC was unreachable.</summary>
    private async Task<bool> RunCycleAsync(DeviceRuntime device, CancellationToken ct)
    {
        var catalog = device.Catalog;
        var samples = new List<TelemetrySample>(catalog.Tags.Count);
        var errors = new List<string?>(catalog.Tags.Count);
        string? unavailableReason = null;
        var plcUnavailable = false;
        var unexpectedLogged = false;

        var readStarted = clock.GetTimestamp();
        var (results, batchFailed) = await ReadAllAsync(device, ct).ConfigureAwait(false);
        var readDuration = clock.GetElapsedTime(readStarted);
        int? requests = !batchFailed && device.Driver is IPlcReadStatistics { LastBatchRequests: >= 0 and var sent } ? sent : null;

        for (var i = 0; i < catalog.Tags.Count; i++)
        {
            var tag = catalog.Tags[i];
            switch (results[i])
            {
                case { Error: null }:
                    var (sample, valueError) = ToSample(tag, results[i].Value);
                    samples.Add(sample);
                    errors.Add(valueError);
                    break;

                case { Error: PlcUnavailableException } when plcUnavailable:
                    // Includes PlcBusyException. The driver skipped the other tags; they would only wait for the same timeout.
                    samples.Add(Bad(tag));
                    errors.Add($"Not read: {unavailableReason}");
                    break;

                case { Error: PlcUnavailableException ex }:
                    logger.LogWarning("PLC {Device} unavailable while reading {TagId}: {Message}", device.Name, tag.TagId, ex.Message);
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
        snapshot.Publish(device.Name, new PollCycle(
            clock.GetUtcNow(),
            plcUnavailable,
            [.. samples.Select((s, i) => new TagReading(s.TagId, s.Value, s.Quality, s.TimestampUtc, errors[i]))],
            readDuration,
            requests));

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
    private static async Task<(IReadOnlyList<PlcReadResult> Results, bool BatchFailed)> ReadAllAsync(DeviceRuntime device, CancellationToken ct)
    {
        var catalog = device.Catalog;
        Exception failure;
        try
        {
            var results = await device.Driver!.ReadAsync(catalog.Tags, ct).ConfigureAwait(false);
            if (results.Count == catalog.Tags.Count)
            {
                return (results, false);
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

        return ([.. catalog.Tags.Select(_ => PlcReadResult.Fail(failure))], true);
    }

    /// <summary>The sample for one read value, and the error that made it Bad (null when Good).</summary>
    private (TelemetrySample Sample, string? Error) ToSample(ResolvedTag tag, object? value)
    {
        var now = clock.GetUtcNow();

        // bool → true/false; integer types → JSON integers; float/double → shortest round-trip number; string → JSON
        // string. Anything the contract would refuse (NaN/±Inf, text over the limit, an unexpected CLR type) is a bad
        // sample, so one odd value cannot invalidate the whole batch.
        var (json, error) = value switch
        {
            bool b => (JsonSerializer.SerializeToElement(b), null),
            byte or ushort or short or uint or int => (JsonSerializer.SerializeToElement(value, value.GetType()), null),
            float real when float.IsFinite(real) => (JsonSerializer.SerializeToElement(real), null),
            double real when double.IsFinite(real) => (JsonSerializer.SerializeToElement(real), null),
            null or float or double => ((JsonElement?)null, "Value is not a finite number."),
            string text when ContractJson.StringValueProblem(text) is { } problem => (null, $"Text {problem}."),
            string text => (JsonSerializer.SerializeToElement(text), null),
            _ => (null, "Value has a type the telemetry contract cannot carry."),
        };

        return json is null
            ? (new TelemetrySample(tag.TagId, null, TelemetryQuality.Bad, now), error)
            : (new TelemetrySample(tag.TagId, json, TelemetryQuality.Good, now), null);
    }

    private TelemetrySample Bad(ResolvedTag tag) => new(tag.TagId, null, TelemetryQuality.Bad, clock.GetUtcNow());
}
