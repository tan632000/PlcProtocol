using System.Text;
using System.Text.Json;
using IndustrialIot.Edge.Commands;
using IndustrialIot.Edge.Drivers;
using IndustrialIot.Edge.Monitoring;
using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace IndustrialIot.Edge.Messaging;

/// <summary>
/// Reports this Edge's PLCs and tags on <see cref="MqttTopics.Inventory"/> as rounds of <see cref="EdgeInventory"/>
/// parts. A round goes out when a connection is (re)established, within 5 s after a device's state changes (at most
/// one round per 5 s), and every 60 s; a <c>lastError</c> change rides the next heartbeat so a flapping tag cannot
/// multiply the traffic. Nothing is buffered: while disconnected no round is attempted, and a failed round is simply
/// replaced by the next one. Each agent start (a settings apply included) is a new <see cref="EdgeInventory.BootId"/>.
/// </summary>
internal sealed class InventoryPublisher(
    MqttConnection connection,
    MqttTopics topics,
    PlcDeviceRegistry registry,
    LiveTagSnapshot snapshot,
    IOptions<EdgeOptions> edge,
    IOptions<CommandsOptions> commands,
    TimeProvider clock,
    ILogger<InventoryPublisher> logger) : BackgroundService
{
    /// <summary>Largest serialized part; leaves 4 KiB of the 16 KiB packet for the MQTT header and topic.</summary>
    internal const int MaxPartBytes = 12 * 1024;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinRoundGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(10);

    private readonly Guid _bootId = Guid.NewGuid();
    private long _round = -1;
    private long _generation;
    private DateTimeOffset? _lastRoundUtc;
    private string? _lastStates;
    private DateTimeOffset? _lastPublishWarning;
    private DateTimeOffset? _lastBuildWarning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await connection.WaitForConnectionAsync(stoppingToken).WaitAsync(CheckInterval, clock, stoppingToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue; // disconnected: no round is attempted
                }

                var now = clock.GetUtcNow();
                var states = StateKey();
                var generation = connection.ConnectionGeneration;
                var due = generation != _generation
                    || _lastRoundUtc is not { } last
                    || now - last >= Heartbeat
                    || (states != _lastStates && now - last >= MinRoundGap);
                if (due)
                {
                    _generation = generation;
                    _lastStates = states;
                    _lastRoundUtc = now;
                    await PublishRoundAsync(now, stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(CheckInterval, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is stopping.
        }
    }

    private async Task PublishRoundAsync(DateTimeOffset now, CancellationToken ct)
    {
        var payloads = BuildRound(now);
        if (payloads is null)
        {
            return;
        }

        try
        {
            foreach (var payload in payloads)
            {
                await connection.PublishAsync(topics.Inventory, payload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Telemetry has its own publisher; a refused or lost round is replaced by the next one.
            Warn(ref _lastPublishWarning, now, "Publishing the inventory failed ({Reason}); the next round replaces it.", ex.GetBaseException().Message);
        }
    }

    /// <summary>Every part of the next round, serialized and validated; null when the round cannot be sent.</summary>
    private List<string>? BuildRound(DateTimeOffset now)
    {
        var devices = registry.Devices.Select(Status).ToList();
        var template = new EdgeInventory
        {
            SchemaVersion = ContractJson.SchemaVersion,
            EdgeId = edge.Value.EdgeId,
            BootId = _bootId,
            Round = _round + 1,
            // The widest values the part numbers can take, so a measured size is never exceeded later.
            Part = EdgeInventory.MaxParts - 1,
            PartCount = EdgeInventory.MaxParts,
            SentUtc = now,
            CommandsEnabled = commands.Value.Enabled,
        };

        var chunks = new List<EdgeInventory>();
        foreach (var statuses in Pack(template with { Devices = [] }, devices))
        {
            chunks.Add(template with { Devices = statuses });
        }

        foreach (var device in registry.Devices)
        {
            var name = device.Name;
            var items = device.Device.Tags.Select(Item).ToList();
            foreach (var group in Pack(template with { Tags = new InventoryTags { Device = name, Items = [] } }, items))
            {
                chunks.Add(template with { Tags = new InventoryTags { Device = name, Items = group } });
            }
        }

        if (chunks.Count > EdgeInventory.MaxParts)
        {
            Warn(ref _lastBuildWarning, now, "The inventory needs {Parts} parts, more than the limit of {Limit}; it is not sent.", chunks.Count, EdgeInventory.MaxParts);
            return null;
        }

        var payloads = new List<string>(chunks.Count);
        try
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                var payload = ContractJson.Serialize(chunks[i] with { Part = i, PartCount = chunks.Count });
                if (Encoding.UTF8.GetByteCount(payload) > MaxPartBytes)
                {
                    throw new InvalidOperationException($"part {i} is larger than {MaxPartBytes} bytes");
                }

                payloads.Add(payload);
            }
        }
        catch (Exception ex) when (ex is ContractValidationException or InvalidOperationException)
        {
            Warn(ref _lastBuildWarning, now, "The inventory cannot be built ({Reason}); it is not sent.", ex.Message);
            return null;
        }

        _round++;
        return payloads;
    }

    /// <summary>
    /// Splits <paramref name="items"/> into consecutive groups whose part stays within <see cref="MaxPartBytes"/>: the
    /// empty part's size plus each item's serialized size plus one comma between items. Always yields at least one group.
    /// </summary>
    private static IEnumerable<List<T>> Pack<T>(EdgeInventory emptyPart, IReadOnlyList<T> items)
    {
        var baseSize = SizeOf(emptyPart);
        var group = new List<T>();
        var size = baseSize;
        foreach (var item in items)
        {
            var itemSize = SizeOf(item);
            var added = group.Count == 0 ? itemSize : itemSize + 1;
            if (group.Count > 0 && size + added > MaxPartBytes)
            {
                yield return group;
                group = [];
                size = baseSize;
                added = itemSize;
            }

            group.Add(item);
            size += added;
        }

        yield return group;
    }

    private static int SizeOf<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options).Length;

    /// <summary>The device as the monitor shows it: disabled, waiting (no cycle yet), connected or unavailable.</summary>
    private InventoryDevice Status(DeviceRuntime device)
    {
        var cycle = device.Driver is null ? null : snapshot.Devices.GetValueOrDefault(device.Name);
        return new InventoryDevice
        {
            Name = device.Name,
            // The catalog's spelling: the configuration may differ in case or carry spaces the model lookup ignores.
            Model = PlcModelCatalog.TryGet(device.Device.Model, out var model) ? model.Name : device.Device.Model,
            Enabled = device.Device.Enabled,
            WritesEnabled = device.Device.WritesEnabled,
            State = State(device, cycle),
            LastGoodUtc = snapshot.LastGoodUtc.TryGetValue(device.Name, out var lastGood) ? lastGood : null,
            LastError = cycle switch
            {
                null => null,
                { PlcUnavailable: true } => InventoryDeviceError.Unavailable,
                _ when cycle.Readings.Any(r => r.Quality != TelemetryQuality.Good) => InventoryDeviceError.TagErrors,
                _ => null,
            },
            TagCount = device.Device.Tags.Count,
        };
    }

    private static InventoryDeviceState State(DeviceRuntime device, PollCycle? cycle) => (device.Driver, cycle) switch
    {
        (null, _) => InventoryDeviceState.Disabled,
        (_, null) => InventoryDeviceState.Waiting,
        (_, { PlcUnavailable: true }) => InventoryDeviceState.Unavailable,
        _ => InventoryDeviceState.Connected,
    };

    /// <summary>Device states only: a <c>lastError</c> change alone does not trigger a round.</summary>
    private string StateKey() => string.Join('|', registry.Devices.Select(d =>
        $"{d.Name}={State(d, d.Driver is null ? null : snapshot.Devices.GetValueOrDefault(d.Name))}"));

    private static InventoryTag Item(Drivers.Models.PlcTagDefinition tag) => new()
    {
        TagId = tag.TagId,
        Name = string.IsNullOrEmpty(tag.Name) ? tag.TagId : tag.Name,
        DataType = tag.DataType.ToString(),
        Length = tag.Length,
        Writable = tag.Writable,
        Min = tag.Min,
        Max = tag.Max,
        Description = Normalize(tag.Description),
    };

    /// <summary>
    /// Control characters and lone surrogates (possible through an environment variable) become spaces, the text is
    /// trimmed and an empty one is dropped; longer than the limit it is cut without splitting a surrogate pair. The
    /// result always passes the contract's plain-text rule.
    /// </summary>
    internal static string? Normalize(string? description)
    {
        if (description is null)
        {
            return null;
        }

        var text = string.Create(description.Length, description, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    span[i] = c;
                    span[++i] = source[i];
                }
                else
                {
                    span[i] = char.IsControl(c) || char.IsSurrogate(c) ? ' ' : c;
                }
            }
        }).Trim();

        if (text.Length > InventoryTag.MaxDescriptionLength)
        {
            var cut = InventoryTag.MaxDescriptionLength;
            if (char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;
            }

            text = text[..cut].TrimEnd();
        }

        return text.Length == 0 ? null : text;
    }

    private void Warn(ref DateTimeOffset? last, DateTimeOffset now, string message, params object?[] args)
    {
        if (last is { } previous && now - previous < WarningInterval)
        {
            return;
        }

        last = now;
#pragma warning disable CA2254 // The two templates are constants of this class.
        logger.LogWarning(message, args);
#pragma warning restore CA2254
    }
}
