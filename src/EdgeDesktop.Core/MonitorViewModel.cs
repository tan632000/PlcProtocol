using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>The colour of a badge, as the web monitor's CSS classes ok / warn / bad / idle.</summary>
public enum BadgeKind
{
    Idle,
    Ok,
    Warn,
    Bad,
}

public sealed record DeviceRow(
    string Name, string Model, string Address, string State, BadgeKind StateKind, string LastGood, string ReadTime, string Requests, string Error);

public sealed record TagRow(
    string TagId, string Device, string Address, string DataType, string Value, string Quality, BadgeKind QualityKind, string Time, string Access, string Error);

/// <summary>
/// What the desktop monitor shows, with the texts and rules of the web monitor (src/Edge/Monitoring/monitor.html,
/// render and refresh). Read-only: it only ever reads <c>GET /api/status</c>.
/// </summary>
public sealed class MonitorViewModel(TimeProvider time) : INotifyPropertyChanged
{
    private const string Dash = "—";

    private static readonly NumberFormatInfo Grouped = new() { NumberGroupSeparator = ".", NumberDecimalDigits = 0 };

    private static readonly Dictionary<string, (string Text, BadgeKind Kind)> PlcStates = new(StringComparer.Ordinal)
    {
        ["ok"] = ("Đọc tốt", BadgeKind.Ok),
        ["partial"] = ("Có tag lỗi", BadgeKind.Warn),
        ["unavailable"] = ("Mất kết nối", BadgeKind.Bad),
        ["waiting"] = ("Chưa có dữ liệu", BadgeKind.Idle),
    };

    private static readonly Dictionary<string, (string Text, BadgeKind Kind)> DeviceStates = new(StringComparer.Ordinal)
    {
        ["connected"] = ("Đang đọc", BadgeKind.Ok),
        ["unavailable"] = ("Mất kết nối", BadgeKind.Bad),
        ["waiting"] = ("Chưa có dữ liệu", BadgeKind.Idle),
        ["disabled"] = ("Đã tắt", BadgeKind.Idle),
    };

    private static readonly Dictionary<string, (string Text, BadgeKind Kind)> Qualities = new(StringComparer.Ordinal)
    {
        ["good"] = ("Tốt", BadgeKind.Ok),
        ["bad"] = ("Lỗi", BadgeKind.Bad),
        ["uncertain"] = ("Không chắc", BadgeKind.Warn),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public string EdgeId { get; private set => Set(ref field, value); } = "";

    public string PlcText { get; private set => Set(ref field, value); } = "…";

    public BadgeKind PlcKind { get; private set => Set(ref field, value); }

    public string PlcDetail { get; private set => Set(ref field, value); } = "";

    public string LastCycle { get; private set => Set(ref field, value); } = Dash;

    public string PollDetail { get; private set => Set(ref field, value); } = "";

    public string MqttText { get; private set => Set(ref field, value); } = "…";

    public BadgeKind MqttKind { get; private set => Set(ref field, value); }

    public string MqttDetail { get; private set => Set(ref field, value); } = "";

    public string Pending { get; private set => Set(ref field, value); } = Dash;

    /// <summary>The failure banner, or null while the last poll succeeded.</summary>
    public string? Banner { get; private set => Set(ref field, value); }

    public string Updated { get; private set => Set(ref field, value); } = "";

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public ObservableCollection<TagRow> Tags { get; } = [];

    /// <summary>The last status applied, for readers that need more than the shown texts (the settings window).</summary>
    public EdgeStatus? LastStatus { get; private set; }

    /// <summary>Raised at the end of <see cref="Apply"/>, once every text and row shows the new status.</summary>
    public event EventHandler? StatusApplied;

    public void Apply(EdgeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var devices = status.Devices ?? [];
        var now = status.NowUtc;
        var last = status.LastCycleUtc;

        EdgeId = status.EdgeId ?? "";
        Banner = null;

        // Plc describes the only PLC; with several PLCs it is null and the device table holds the details.
        if (status.Plc is { } plc)
        {
            // Overdue: well past one interval plus a full PLC timeout, measured on the Edge's clock (nowUtc).
            var stale = last is { } at && (now - at).TotalMilliseconds > 3 * (status.PollIntervalMs ?? 0) + plc.TimeoutMs;
            (PlcText, PlcKind) = stale ? ("Dữ liệu cũ", BadgeKind.Warn) : Word(PlcStates, plc.State);
            var rackSlot = plc.Rack is null || plc.Slot is null ? "" : $" · Rack {plc.Rack} / Slot {plc.Slot}";
            PlcDetail = $"{plc.Protocol} · {plc.Host}:{plc.Port}{rackSlot}";
            PollDetail = last is { } cycle
                ? $"{SecondsAgo(now, cycle)} giây trước · đọc {Number(status.ReadMs)} ms · chu kỳ {status.PollIntervalMs} ms"
                : $"Chu kỳ {status.PollIntervalMs} ms";
        }
        else
        {
            var down = devices.Count(d => d.State == "unavailable");
            (PlcText, PlcKind) = down > 0 ? ($"{down} PLC mất kết nối", BadgeKind.Bad) : ("Đọc tốt", BadgeKind.Ok);
            PlcDetail = $"{devices.Count} PLC";
            PollDetail = last is { } cycle ? $"{SecondsAgo(now, cycle)} giây trước · từng PLC ở bảng dưới" : "";
        }

        LastCycle = Time(last);
        (MqttText, MqttKind) = status.Mqtt?.Connected == true ? ("Đã kết nối", BadgeKind.Ok) : ("Chưa kết nối", BadgeKind.Warn);
        MqttDetail = status.Mqtt is { } mqtt ? $"{mqtt.Host}:{mqtt.Port}" : "";
        Pending = status.BufferPending is { } pending ? pending.ToString("N", Grouped) : "?";

        Sync(Devices, [.. devices.Select(Row)]);
        Sync(Tags, [.. (status.Tags ?? []).Select(Row)]);
        Updated = "Cập nhật " + time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        LastStatus = status;
        StatusApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the banner and keeps the last data, as the web monitor does.</summary>
    public void ShowFailure(string reason) =>
        Banner = $"Không lấy được dữ liệu từ phần mềm Edge ({reason}). Service IndustrialIot.Edge có đang chạy không?";

    /// <summary>
    /// Polls until <paramref name="ct"/> is cancelled: one request, then 1 s after it finished the next, so requests
    /// never overlap. Every change to this view model runs inside <paramref name="post"/>, which the window sets to its
    /// UI thread; once cancelled, nothing changes any more, not even from an action already posted.
    /// </summary>
    public async Task RunAsync(StatusClient client, Action<Action> post, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(post);
        while (true)
        {
            var result = await client.GetAsync(ct).ConfigureAwait(false);
            if (result.Cancelled || ct.IsCancellationRequested)
            {
                return;
            }

            post(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (result.Status is { } status)
                {
                    Apply(status);
                }
                else
                {
                    ShowFailure(result.FailureReason!);
                }
            });

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private DeviceRow Row(DeviceInfo d)
    {
        var (state, kind) = Word(DeviceStates, d.State);
        return new DeviceRow(
            d.Name, d.Model, $"{d.Host}:{d.Port}", state, kind, Time(d.LastGoodUtc),
            d.ReadMs is null ? Dash : $"{Number(d.ReadMs)} ms",
            d.Requests is { } requests ? requests.ToString(CultureInfo.InvariantCulture) : Dash,
            d.LastError ?? "");
    }

    private TagRow Row(TagInfo t)
    {
        var (quality, kind) = t.Quality is null ? (Dash, BadgeKind.Idle) : Word(Qualities, t.Quality);
        return new TagRow(
            t.TagId, t.Device, t.Address, t.DataType, Value(t.Value, t.DataType), quality, kind,
            Time(t.TimestampUtc), t.Writable ? "Cho phép" : "Chỉ đọc", t.Error ?? "");
    }

    private string Time(DateTimeOffset? utc) => utc is { } at
        ? TimeZoneInfo.ConvertTime(at, time.LocalTimeZone).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
        : Dash;

    private static (string Text, BadgeKind Kind) Word(Dictionary<string, (string, BadgeKind)> words, string? key) =>
        key is not null && words.TryGetValue(key, out var word) ? word : (key ?? "", BadgeKind.Idle);

    private static string Value(JsonElement? value, string dataType) => value switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => Dash,
        { ValueKind: JsonValueKind.True or JsonValueKind.False } v when dataType == "Bool" => v.GetBoolean() ? "TRUE" : "FALSE",
        { ValueKind: JsonValueKind.String } v => v.GetString() ?? "",
        { } v => v.GetRawText(),
    };

    private static string SecondsAgo(DateTimeOffset now, DateTimeOffset last) =>
        ((now - last).TotalMilliseconds / 1000).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Number(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? Dash;

    /// <summary>
    /// Brings <paramref name="target"/> to <paramref name="rows"/> by index, replacing only rows that changed, so a
    /// table refreshed every second keeps its scroll position and selection.
    /// </summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= target.Count)
            {
                target.Add(rows[i]);
            }
            else if (!EqualityComparer<T>.Default.Equals(target[i], rows[i]))
            {
                target[i] = rows[i];
            }
        }

        while (target.Count > rows.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
