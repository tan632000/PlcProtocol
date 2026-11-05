using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace IndustrialIot.EdgeDesktop.Core;

public enum TreeNodeKind
{
    AddChannel,
    Channel,
    Device,
}

/// <summary>A node of the left tree: the "add channel" entry, a channel, or a PLC with its state text.</summary>
public sealed class TreeNode : INotifyPropertyChanged
{
    internal TreeNode(TreeNodeKind kind, string text, DraftChannel? channel = null, DraftDevice? device = null)
    {
        Kind = kind;
        Text = text;
        Channel = channel;
        Device = device;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TreeNodeKind Kind { get; }

    public string Text { get; }

    /// <summary>The draft channel of a channel node.</summary>
    public DraftChannel? Channel { get; }

    /// <summary>The draft device of a PLC node.</summary>
    public DraftDevice? Device { get; }

    public ObservableCollection<TreeNode> Children { get; } = [];

    /// <summary>Bound to the tree item, so a selection made by the view model (after a rebuild) shows in the tree.</summary>
    public bool IsSelected { get; set => Set(ref field, value); }

    public bool IsExpanded { get; set => Set(ref field, value); } = true;

    public string State { get; internal set => Set(ref field, value); } = "";

    public BadgeKind StateKind { get; internal set => Set(ref field, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>A row of the tag grid: the draft tag with its live reading, or dashes for a tag the Edge does not read yet.</summary>
public sealed record GridRow(
    DraftTag Tag, string Name, string Address, string DataType, string Access, string Value, string Quality, BadgeKind QualityKind, string Time, string Error);

public sealed record LogEntry(string Time, string Source, string Message);

/// <summary>
/// The main window: the tree Channel → PLC of the draft with the PLC table's state texts, the selected PLC's tags with
/// their live values (matched by TagId), the event log, the status bar and title, and the commands that open the dialogs
/// on the draft. It reads the Edge through the one poll loop of <see cref="MonitorViewModel"/>; every change to it runs
/// on the window's thread.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    public const int LogLimit = 500;

    private const string Dash = "—";
    private const string Detached = "Mục này không còn trong cấu hình nháp, thay đổi bị bỏ qua";

    private readonly SettingsClient _client;
    private readonly IUserPrompts _prompts;
    private readonly IDialogOpener _dialogs;
    private readonly TimeProvider _time;
    private readonly int _port;

    // The last state text of each PLC, to log a change once.
    private readonly Dictionary<string, string> _plcStates = new(StringComparer.Ordinal);
    private bool _edgeLost;

    public MainViewModel(MonitorViewModel monitor, SettingsClient client, IUserPrompts prompts, IDialogOpener dialogs, TimeProvider time, int port)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(time);
        Monitor = monitor;
        _client = client;
        _prompts = prompts;
        _dialogs = dialogs;
        _time = time;
        _port = port;
        monitor.PropertyChanged += OnMonitorChanged;
        monitor.StatusApplied += OnStatusApplied;
        DraftChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MonitorViewModel Monitor { get; }

    /// <summary>The operator's draft, or null until the settings have been read.</summary>
    public ConfigDraft? Draft { get; private set; }

    public ObservableCollection<TreeNode> Tree { get; } = [];

    public ObservableCollection<GridRow> Tags { get; } = [];

    /// <summary>Newest first, at most <see cref="LogLimit"/> entries, in memory only.</summary>
    public ObservableCollection<LogEntry> Log { get; } = [];

    public TreeNode? SelectedNode
    {
        get;
        set
        {
            if (field != value)
            {
                field?.IsSelected = false;
                field = value;
                field?.IsSelected = true;
                SelectedTag = null;
                RefreshGrid();
                Changed();
            }
        }
    }

    public GridRow? SelectedTag
    {
        get;
        set
        {
            field = value;
            Changed();
        }
    }

    public string? GridPlaceholder => SelectedNode?.Device is null ? "Chọn một PLC để xem tag" : null;

    public string Title =>
        (Monitor.EdgeId.Length == 0 ? "IIoT Edge Monitor" : $"IIoT Edge Monitor — {Monitor.EdgeId}") + (Draft?.IsDirty == true ? " *" : "");

    /// <summary>The first status bar field: the Edge, or the failure banner while the status cannot be read.</summary>
    public string EdgeField => Monitor.Banner ?? $"Edge: {(Monitor.EdgeId.Length == 0 ? "…" : Monitor.EdgeId)}";

    public bool EdgeFieldIsError => Monitor.Banner is not null;

    public string Endpoint => $"127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}";

    public string MqttField => $"MQTT: {Monitor.MqttText}";

    public string BufferField => $"Buffer: {Monitor.Pending}";

    public string CycleField => $"Chu kỳ gần nhất: {Monitor.LastCycle}";

    public string CommandsField => Draft is null ? "Lệnh ghi từ Cloud: ?" : $"Lệnh ghi từ Cloud: {(Draft.CommandsEnabled ? "bật" : "tắt")}";

    public string SaveField => Draft?.IsDirty == true ? "Có thay đổi chưa lưu" : "Đã lưu";

    public bool CanAddChannel => Draft is not null;

    public bool CanAddDevice => Draft is not null && SelectedNode is { Kind: TreeNodeKind.Channel or TreeNodeKind.Device };

    public bool CanAddTag => Draft is not null && SelectedNode is { Kind: TreeNodeKind.Device };

    public bool CanEdit => Draft is not null && (SelectedTag is not null || SelectedNode is { Kind: not TreeNodeKind.AddChannel });

    public bool CanDelete => CanEdit;

    public bool CanSave => Draft is { IsDirty: true, IsStale: false };

    public bool CanCheck => Draft is not null;

    public bool CanTestConnection => CanAddTag;

    /// <summary>Reads the Edge's settings into a new, clean draft.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var load = await _client.GetAsync(ct).ConfigureAwait(true);
        if (load.Snapshot is not { } snapshot)
        {
            var reason = load.Refusal is { } refusal ? RefusalMessages.Describe(refusal) : "?";
            AddLog("Cấu hình", $"Không tải được cấu hình: {reason}");
            return;
        }

        Draft = new ConfigDraft(snapshot);
        SelectedNode = null;
        DraftChanged();
    }

    /// <summary>Double-click or Enter on a node: the first node adds a channel, the others open their Edit dialog.</summary>
    public Task ActivateAsync(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        SelectedNode = node;
        SelectedTag = null; // the node's own dialog, even while a tag of that PLC is still selected in the grid
        return node.Kind == TreeNodeKind.AddChannel ? AddChannelAsync() : EditAsync();
    }

    public Task AddChannelAsync() =>
        Draft is { } draft ? OpenAsync(() => Request(new ChannelDialogViewModel(draft, null))) : Task.CompletedTask;

    public Task AddDeviceAsync()
    {
        if (Draft is not { } draft || !CanAddDevice)
        {
            return Task.CompletedTask;
        }

        var node = SelectedNode!;
        return OpenAsync(() => Request(new DeviceDialogViewModel(
            draft, node.Device is { } device ? draft.ChannelOf(device) : node.Channel!, null, _client, _prompts)));
    }

    public Task AddTagAsync() =>
        Draft is { } draft && SelectedNode?.Device is { } device
            ? OpenAsync(() => Request(new TagDialogViewModel(draft, device, null, _client)))
            : Task.CompletedTask;

    /// <summary>Edits the selected tag, or else the selected channel or PLC.</summary>
    public Task EditAsync()
    {
        if (Draft is not { } draft)
        {
            return Task.CompletedTask;
        }

        if (SelectedTag is { } row)
        {
            return OpenAsync(() => Request(new TagDialogViewModel(draft, draft.DeviceOf(row.Tag), row.Tag, _client)));
        }

        if (SelectedNode?.Channel is { } channel)
        {
            return OpenAsync(() => Request(new ChannelDialogViewModel(draft, channel)));
        }

        return EditDeviceAsync();
    }

    /// <summary>Công cụ → Test kết nối PLC: the selected PLC's dialog, which holds the test.</summary>
    public Task TestConnectionAsync() => EditDeviceAsync();

    /// <summary>Removes the selected tag, or else the selected channel or PLC, from the draft after the confirmation.</summary>
    public async Task DeleteAsync()
    {
        if (Draft is not { } draft)
        {
            return;
        }

        string message;
        Action remove;
        if (SelectedTag is { } row)
        {
            message = Confirmations.Delete(row.Tag);
            remove = () => draft.RemoveTag(row.Tag);
        }
        else if (SelectedNode?.Channel is { } channel)
        {
            message = Confirmations.Delete(channel);
            remove = () => draft.RemoveChannel(channel);
        }
        else if (SelectedNode?.Device is { } device)
        {
            message = Confirmations.Delete(device);
            remove = () => draft.RemoveDevice(device);
        }
        else
        {
            return;
        }

        if (!await _prompts.ConfirmAsync(message).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            remove();
        }
        catch (InvalidOperationException)
        {
            AddLog("Cấu hình", Detached);
        }

        DraftChanged();
    }

    /// <summary>Kiểm tra cấu hình: validates the draft and logs each error at its node (or its path), or "Hợp lệ".</summary>
    public async Task CheckAsync()
    {
        if (Draft is not { } draft)
        {
            return;
        }

        var result = await _client.ValidateAsync(draft).ConfigureAwait(true);
        if (result.Refusal is { } refusal)
        {
            AddLog("Kiểm tra", $"Không kiểm tra được: {RefusalMessages.Describe(refusal)}");
            return;
        }

        if (result.Errors.Count == 0)
        {
            AddLog("Kiểm tra", "Hợp lệ");
            return;
        }

        foreach (var error in result.Errors)
        {
            AddLog("Kiểm tra", Describe(draft, error));
        }
    }

    private Task EditDeviceAsync() =>
        Draft is { } draft && SelectedNode?.Device is { } device
            ? OpenAsync(() => Request(new DeviceDialogViewModel(draft, draft.ChannelOf(device), device, _client, _prompts)))
            : Task.CompletedTask;

    private static string Describe(ConfigDraft draft, ApiError error)
    {
        if (draft.Locate(error.Path) is not { } at)
        {
            return $"{error.Path}: {error.Message}";
        }

        var where = $"Channel {at.Channel.Name}";
        if (at.Device is { } device)
        {
            where += $" / PLC {device.Name}";
        }

        if (at.Tag is { } tag)
        {
            where += $" / tag {tag.Name}";
        }

        if (at.Field is { } field)
        {
            where += $" ({field})";
        }

        return $"{where}: {error.Message}";
    }

    /// <summary>Builds the dialog (which may meet a node no longer in the draft), shows it, then shows the draft again.</summary>
    private async Task OpenAsync(Func<DialogRequest> build)
    {
        DialogRequest request;
        try
        {
            request = build();
        }
        catch (InvalidOperationException)
        {
            AddLog("Cấu hình", Detached);
            return;
        }

        await _dialogs.OpenAsync(request).ConfigureAwait(true);
        DraftChanged();
    }

    private DialogRequest Request(ChannelDialogViewModel dialog) => new(dialog, Guard(dialog.Apply));

    private DialogRequest Request(TagDialogViewModel dialog) => new(dialog, Guard(dialog.Apply), Check: Guard(dialog.CheckAsync));

    private DialogRequest Request(DeviceDialogViewModel dialog)
    {
        dialog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceDialogViewModel.TestMessage) && dialog.TestMessage is { } result)
            {
                AddLog($"PLC {dialog.Name.Trim()}", $"Test kết nối: {result}");
            }
        };
        return new(dialog, Guard(dialog.Apply), TestConnection: Guard(dialog.TestConnectionAsync));
    }

    /// <summary>OK/Áp dụng: a node gone from the draft is logged and the dialog closes, since nothing is left to edit.</summary>
    private Func<bool> Guard(Func<bool> apply) => () =>
    {
        try
        {
            var applied = apply();
            if (applied)
            {
                DraftChanged(); // Áp dụng in Add mode keeps the dialog open: show each added tag at once
            }

            return applied;
        }
        catch (InvalidOperationException)
        {
            AddLog("Cấu hình", Detached);
            return true;
        }
    };

    private Func<Task> Guard(Func<Task> action) => async () =>
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            AddLog("Cấu hình", Detached);
        }
    };

    private void OnMonitorChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Every other text changes inside Apply, which ends with StatusApplied; only the banner changes outside it.
        if (e.PropertyName != nameof(MonitorViewModel.Banner))
        {
            return;
        }

        var lost = Monitor.Banner is not null;
        if (lost != _edgeLost)
        {
            _edgeLost = lost;
            AddLog("Edge", lost ? "Mất kết nối tới Edge" : "Đã kết nối lại Edge");
        }

        Changed();
    }

    private void OnStatusApplied(object? sender, EventArgs e)
    {
        foreach (var row in Monitor.Devices)
        {
            if (_plcStates.TryGetValue(row.Name, out var old) && old != row.State)
            {
                AddLog($"PLC {row.Name}", $"{old} → {row.State}");
            }

            _plcStates[row.Name] = row.State;
        }

        UpdateStates();
        RefreshGrid();
        Changed();
    }

    /// <summary>Rebuilds the tree from the draft, keeping the selection on the same draft node while it still exists.</summary>
    private void DraftChanged()
    {
        var selected = SelectedNode;
        var selectedTag = SelectedTag?.Tag;
        var collapsed = Tree.Where(n => n is { Channel: not null, IsExpanded: false }).Select(n => n.Channel).ToHashSet();
        Tree.Clear();
        Tree.Add(new TreeNode(TreeNodeKind.AddChannel, "Bấm để thêm Channel"));
        foreach (var channel in Draft?.Channels ?? [])
        {
            var driver = ChannelDialogViewModel.Drivers.FirstOrDefault(d => d.Driver == channel.Driver)?.Display ?? channel.Driver;
            var node = new TreeNode(TreeNodeKind.Channel, $"{channel.Name} ({driver})", channel: channel)
            {
                IsExpanded = !collapsed.Contains(channel),
            };
            foreach (var device in channel.Devices)
            {
                node.Children.Add(new TreeNode(TreeNodeKind.Device, device.Name, device: device));
            }

            Tree.Add(node);
        }

        UpdateStates();
        // The new node is a different object, so the setter marks it selected for the tree.
        SelectedNode = selected is null
            ? null
            : Tree.Concat(Tree.SelectMany(n => n.Children)).FirstOrDefault(n =>
                n.Kind == selected.Kind && n.Channel == selected.Channel && n.Device == selected.Device);
        RefreshGrid();
        SelectedTag = Tags.FirstOrDefault(r => r.Tag == selectedTag && selectedTag is not null);
        Changed();
    }

    private void UpdateStates()
    {
        foreach (var node in Tree.SelectMany(n => n.Children))
        {
            (node.State, node.StateKind) = StateOf(node.Device!);
        }
    }

    private (string Text, BadgeKind Kind) StateOf(DraftDevice device)
    {
        if (Monitor.LastStatus is not { } status)
        {
            return ("…", BadgeKind.Idle);
        }

        if (Monitor.Devices.FirstOrDefault(d => d.Name == device.Name) is not { } row)
        {
            return ("Chưa lưu", BadgeKind.Idle);
        }

        var tagNotGood = (status.Tags ?? []).Any(t => t.Device == device.Name && t.Quality is not null && t.Quality != "good");
        return row.State == "Đang đọc" && tagNotGood ? ("Có tag lỗi", BadgeKind.Warn) : (row.State, row.StateKind);
    }

    private void RefreshGrid()
    {
        var keep = SelectedTag?.Tag;
        List<GridRow> rows = SelectedNode?.Device is { } device ? [.. device.Tags.Select(t => Row(device, t))] : [];
        Sync(Tags, rows);

        // The grid drops its selection when a selected row is replaced by a newer reading; put it back.
        if (keep is not null)
        {
            SelectedTag = Tags.FirstOrDefault(r => r.Tag == keep);
        }
    }

    private GridRow Row(DraftDevice device, DraftTag tag)
    {
        // The Edge's TagId rule: the bare name with LegacyTagIds, else {Device}.{Name}.
        var tagId = device.LegacyTagIds ? tag.Name : $"{device.Name}.{tag.Name}";
        // The device too: two legacy PLCs may each have a tag of that name, saved on one and only drafted on the other.
        var live = Monitor.Tags.FirstOrDefault(t => t.TagId == tagId && t.Device == device.Name);
        return new GridRow(
            tag, tag.Name, tag.Address, tag.DataType, tag.Writable ? "Read/Write" : "Read Only",
            live?.Value ?? Dash, live?.Quality ?? Dash, live?.QualityKind ?? BadgeKind.Idle, live?.Time ?? Dash, live?.Error ?? "");
    }

    private void AddLog(string source, string message)
    {
        Log.Insert(0, new LogEntry(_time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture), source, message));
        while (Log.Count > LogLimit)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    /// <summary>Brings <paramref name="target"/> to <paramref name="rows"/> by index, replacing only rows that changed.</summary>
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

    // The window re-reads every computed text and rule; they all follow from the draft, the selection and the monitor.
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
