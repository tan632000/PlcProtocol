using System.Globalization;

namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>
/// Add or edit a PLC of a channel: only the models of the channel's driver, their defaults, Rack/Slot for S7 only, and
/// writes only where the model allows them (the Edge checks again on save). Test kết nối sends the dialog's current
/// values with a password asked once; a cancelled or empty password sends nothing.
/// </summary>
public sealed class DeviceDialogViewModel : DialogViewModel
{
    private static readonly Dictionary<string, string> ProposedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["driver"] = "Model",
        ["model"] = "Model",
        ["host"] = "Host",
        ["port"] = "Port",
        ["rack"] = "Rack",
        ["slot"] = "Slot",
        ["timeoutMs"] = "TimeoutMs",
    };

    private readonly ConfigDraft _draft;
    private readonly DraftChannel _channel;
    private readonly DraftDevice? _target;
    private readonly SettingsClient _client;
    private readonly IUserPrompts _prompts;
    private bool _modelChosen; // the first model comes from the PLC or the constructor's defaults, not from a choice

    public DeviceDialogViewModel(ConfigDraft draft, DraftChannel channel, DraftDevice? target, SettingsClient client, IUserPrompts prompts)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(channel);
        _draft = draft;
        _channel = channel;
        _target = target;
        _client = client;
        _prompts = prompts;
        Models = [.. draft.Models.Where(m => m.Driver == channel.Driver)];
        ShowLegacyTagIds = target?.LegacyTagIds == true;

        if (target is null)
        {
            // As the web page: numbered over every channel, since PLC names are unique across the Edge.
            Name = $"Plc{draft.Channels.Sum(c => c.Devices.Count) + 1}";
            Host = "";
            Model = Models.FirstOrDefault()?.Name ?? "";
            FillDefaults();
            TimeoutText = "3000";
            PollText = "1000";
            Enabled = true;
        }
        else
        {
            Name = target.Name;
            Model = target.Model;
            Host = target.Host;
            PortText = Text(target.Port);
            RackText = Text(target.Rack);
            SlotText = Text(target.Slot);
            TimeoutText = Text(target.TimeoutMs);
            PollText = Text(target.PollIntervalMs);
            Enabled = target.Enabled;
            WritesEnabled = target.WritesEnabled;
            LegacyTagIds = target.LegacyTagIds;
        }
    }

    public IReadOnlyList<PlcModelInfo> Models { get; }

    public bool IsAddMode => _target is null;

    public string Name { get; set => Set(ref field, value); } = "";

    /// <summary>Choosing another model fills its port, rack and slot, as the web page does.</summary>
    public string Model
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            Set(ref field, value);
            if (_modelChosen)
            {
                FillDefaults();
            }

            _modelChosen = true;

            if (!WritesAllowed)
            {
                WritesEnabled = false;
            }

            OnPropertyChanged(nameof(PortHint));
            OnPropertyChanged(nameof(WritesAllowed));
            OnPropertyChanged(nameof(WritesNote));
            OnPropertyChanged(nameof(CanEnableWrites));
        }
    } = "";

    public string Host { get; set => Set(ref field, value); } = "";

    public string PortText { get; set => Set(ref field, value); } = "";

    public string RackText { get; set => Set(ref field, value); } = "";

    public string SlotText { get; set => Set(ref field, value); } = "";

    public string TimeoutText { get; set => Set(ref field, value); } = "";

    public string PollText { get; set => Set(ref field, value); } = "";

    public bool Enabled { get; set => Set(ref field, value); }

    /// <summary>Cannot be switched on for a model whose writes are not verified; it can always be switched off.</summary>
    public bool WritesEnabled
    {
        get;
        set
        {
            if (value && !WritesAllowed)
            {
                OnPropertyChanged(nameof(WritesEnabled));
                return;
            }

            Set(ref field, value);
            OnPropertyChanged(nameof(CanEnableWrites));
        }
    }

    public bool LegacyTagIds
    {
        get;
        set
        {
            Set(ref field, value);
            OnPropertyChanged(nameof(LegacyTagIdsWarning));
        }
    }

    /// <summary>Only a PLC migrated from a single-PLC Edge shows it.</summary>
    public bool ShowLegacyTagIds { get; }

    public string? LegacyTagIdsWarning => ShowLegacyTagIds && !LegacyTagIds
        ? "Bỏ chọn: Cloud sẽ thấy TagId mới ({PLC}.{tag}); lịch sử cũ vẫn nằm dưới TagId cũ."
        : null;

    public bool IsS7 => _channel.Driver == "S7";

    public string? PortHint => ModelInfo?.PortHint;

    public bool WritesAllowed => ModelInfo?.WritesAllowed ?? false;

    public bool CanEnableWrites => WritesAllowed || WritesEnabled;

    public string? WritesNote => WritesAllowed ? null : $"{Model} chưa cho phép ghi (chưa kiểm chứng trên PLC thật)";

    /// <summary>The outcome of the last Test kết nối, or null.</summary>
    public string? TestMessage { get; private set => Set(ref field, value); }

    private PlcModelInfo? ModelInfo => _draft.Models.FirstOrDefault(m => m.Name == Model);

    public bool Apply()
    {
        if (Read() is not { } edited)
        {
            return false;
        }

        if (_target is null)
        {
            _draft.AddDevice(_channel, edited);
        }
        else
        {
            _draft.Update(_target, edited);
        }

        return true;
    }

    public async Task TestConnectionAsync()
    {
        if (Read() is not { } device)
        {
            return;
        }

        var password = await _prompts.AskPasswordAsync($"Test kết nối PLC {device.Name}").ConfigureAwait(true);
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        var connection = new PlcConnection(_channel.Driver, device.Model, device.Host, device.Port, device.Rack, device.Slot, device.TimeoutMs);
        var result = await _client.TestConnectionAsync(connection, password).ConfigureAwait(true);
        if (result.Refusal is null)
        {
            var ms = result.ElapsedMs is { } elapsed ? $" ({Math.Round(elapsed).ToString(CultureInfo.InvariantCulture)} ms)" : "";
            TestMessage = result.Reachable ? $"Kết nối được{ms}" : $"Không kết nối được tới PLC{ms}";
            return;
        }

        if (result.Refusal.Kind == RefusalKind.Invalid)
        {
            var errors = new Dictionary<string, string>();
            foreach (var error in result.Refusal.Errors)
            {
                var key = error.Path.StartsWith("proposed:", StringComparison.Ordinal) ? error.Path["proposed:".Length..] : error.Path;
                errors[ProposedFields.GetValueOrDefault(key, key)] = error.Message;
            }

            SetErrors(errors);
        }

        TestMessage = result.Refusal.Kind == RefusalKind.Failed
            ? $"Không test được: {RefusalMessages.Describe(result.Refusal)}"
            : RefusalMessages.Describe(result.Refusal);
    }

    /// <summary>The dialog's values as a device, or null with the field errors set.</summary>
    private DraftDevice? Read()
    {
        var errors = new Dictionary<string, string>();
        var device = new DraftDevice
        {
            Name = Name.Trim(),
            Model = Model,
            Host = Host.Trim(),
            Port = Whole(PortText, "Port", required: false, errors),
            Rack = IsS7 ? Whole(RackText, "Rack", required: false, errors) : null,
            Slot = IsS7 ? Whole(SlotText, "Slot", required: false, errors) : null,
            TimeoutMs = Whole(TimeoutText, "TimeoutMs", required: true, errors) ?? 0,
            PollIntervalMs = Whole(PollText, "PollIntervalMs", required: true, errors) ?? 0,
            Enabled = Enabled,
            WritesEnabled = WritesEnabled,
            LegacyTagIds = LegacyTagIds,
        };
        SetErrors(errors);
        return errors.Count == 0 ? device : null;
    }

    private void FillDefaults()
    {
        var model = ModelInfo;
        PortText = Text(model?.DefaultPort);
        RackText = Text(model?.DefaultRack);
        SlotText = Text(model?.DefaultSlot);
    }
}
