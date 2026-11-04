using System.Text.Json;

namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>
/// Add or edit a tag of a PLC: the model's data types, a length for String only, writes only where the model allows
/// them, limits only for a writable tag. Kiểm tra checks the whole draft with this tag on the Edge, without changing the
/// draft, and shows only this tag's errors. Áp dụng in Add mode keeps the dialog open for the next tag.
/// </summary>
public sealed class TagDialogViewModel : DialogViewModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConfigDraft _draft;
    private readonly DraftDevice _device;
    private readonly DraftTag? _target;
    private readonly SettingsClient _client;
    private readonly PlcModelInfo? _model;

    public TagDialogViewModel(ConfigDraft draft, DraftDevice device, DraftTag? target, SettingsClient client)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(device);
        _draft = draft;
        _device = device;
        _target = target;
        _client = client;
        _model = draft.Models.FirstOrDefault(m => m.Name == device.Model);
        DataTypes = _model?.DataTypes ?? [];
        if (target is null)
        {
            DataType = DataTypes.Contains("Int") ? "Int" : DataTypes.FirstOrDefault() ?? "";
        }
        else
        {
            Name = target.Name;
            Address = target.Address;
            DataType = target.DataType;
            LengthText = Text(target.Length);
            Writable = target.Writable;
            MinText = Text(target.Min);
            MaxText = Text(target.Max);
            Description = target.Description ?? "";
        }
    }

    public IReadOnlyList<string> DataTypes { get; }

    public bool IsAddMode => _target is null;

    /// <summary>Edit mode closes on Áp dụng; Add mode stays open for the next tag.</summary>
    public bool CloseAfterApply => _target is not null;

    public string Name { get; set => Set(ref field, value); } = "";

    public string Address { get; set => Set(ref field, value); } = "";

    public string DataType
    {
        get;
        set
        {
            Set(ref field, value);
            OnPropertyChanged(nameof(IsString));
        }
    } = "";

    public bool IsString => DataType == "String";

    public string LengthText { get; set => Set(ref field, value); } = "";

    /// <summary>Cannot be switched on where the model does not allow writes; it can always be switched off.</summary>
    public bool Writable
    {
        get;
        set
        {
            if (value && !WritesAllowed)
            {
                OnPropertyChanged(nameof(Writable));
                return;
            }

            Set(ref field, value);
            OnPropertyChanged(nameof(ShowLimits));
            OnPropertyChanged(nameof(CanWrite));
        }
    }

    public bool CanWrite => WritesAllowed || Writable;

    public bool ShowLimits => Writable;

    public string MinText { get; set => Set(ref field, value); } = "";

    public string MaxText { get; set => Set(ref field, value); } = "";

    public string Description { get; set => Set(ref field, value); } = "";

    /// <summary>The outcome of Kiểm tra or Áp dụng, or null.</summary>
    public string? Message { get; private set => Set(ref field, value); }

    private bool WritesAllowed => _model?.WritesAllowed ?? false;

    public async Task CheckAsync()
    {
        if (Read() is not { } edited)
        {
            return;
        }

        // A copy of the draft with this tag in place: the Edge judges the whole tree, the draft stays as it is.
        var copy = new ConfigDraft(new SettingsSnapshot(
            _draft.Revision,
            "draft",
            _draft.ToSettingsJson().Deserialize<ChannelTree>(Json)!,
            _draft.Models,
            _draft.Warnings,
            _draft.CommandsEnabled,
            _draft.PasswordSet));
        var channel = _draft.ChannelOf(_device);
        var copyDevice = copy.Channels[IndexOf(_draft.Channels, channel)].Devices[IndexOf(channel.Devices, _device)];
        DraftTag copyTag;
        if (_target is null)
        {
            copyTag = copy.AddTag(copyDevice, edited);
        }
        else
        {
            copyTag = copyDevice.Tags[IndexOf(_device.Tags, _target)];
            copy.Update(copyTag, edited);
        }

        var result = await _client.ValidateAsync(copy).ConfigureAwait(true);
        if (result.Refusal is not null)
        {
            Message = RefusalMessages.Describe(result.Refusal);
            return;
        }

        var errors = new Dictionary<string, string>();
        string? whole = null; // an error of the tag itself, not of one of its fields
        foreach (var error in result.Errors)
        {
            if (copy.Locate(error.Path) is { } location && ReferenceEquals(location.Tag, copyTag))
            {
                if (location.Field is { } field)
                {
                    errors[field] = error.Message;
                }
                else
                {
                    whole ??= error.Message;
                }
            }
        }

        SetErrors(errors);
        Message = whole ?? (errors.Count == 0 ? "Hợp lệ" : "Có ô chưa hợp lệ");
    }

    public bool Apply()
    {
        if (Read() is not { } edited)
        {
            return false;
        }

        if (_target is null)
        {
            _draft.AddTag(_device, edited);
            Message = $"Đã thêm tag {edited.Name}";
            Name = "";
            Address = "";
        }
        else
        {
            _draft.Update(_target, edited);
        }

        return true;
    }

    private DraftTag? Read()
    {
        var errors = new Dictionary<string, string>();
        var tag = new DraftTag
        {
            Name = Name.Trim(),
            Address = Address.Trim(),
            DataType = DataType,
            Length = IsString ? Whole(LengthText, "Length", required: false, errors) : null,
            Writable = Writable,
            Min = Writable ? Fraction(MinText, "Min", errors) : null,
            Max = Writable ? Fraction(MaxText, "Max", errors) : null,
            Description = Description.Trim().Length == 0 ? null : Description,
        };
        SetErrors(errors);
        return errors.Count == 0 ? tag : null;
    }

    private static int IndexOf<T>(IReadOnlyList<T> items, T item)
        where T : class
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                return i;
            }
        }

        throw new InvalidOperationException("The node is no longer part of this draft.");
    }
}
