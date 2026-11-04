namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>A driver as the API names it and as the operator reads it.</summary>
public sealed record DriverChoice(string Driver, string Display);

/// <summary>
/// Add or edit a channel: its name and its driver, picked in the driver dialog with the models it supports. The driver of
/// a channel that has PLCs cannot change (its PLCs' models belong to it).
/// </summary>
public sealed class ChannelDialogViewModel : DialogViewModel
{
    public static readonly IReadOnlyList<DriverChoice> Drivers =
    [
        new("S7", "Siemens S7"),
        new("Slmp3E", "Mitsubishi SLMP 3E"),
        new("Mc1E", "Mitsubishi MC 1E"),
    ];

    private readonly ConfigDraft _draft;
    private readonly DraftChannel? _target;

    public ChannelDialogViewModel(ConfigDraft draft, DraftChannel? target)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _draft = draft;
        _target = target;
        Name = target?.Name ?? $"Channel{draft.Channels.Count + 1}";
        Driver = target?.Driver ?? Drivers[0].Driver;
    }

    public bool IsAddMode => _target is null;

    public string Name { get; set => Set(ref field, value); }

    public string Driver
    {
        get;
        private set
        {
            Set(ref field, value);
            OnPropertyChanged(nameof(DriverDisplay));
            OnPropertyChanged(nameof(DriverNote));
        }
    }

    public string DriverDisplay => Drivers.FirstOrDefault(d => d.Driver == Driver)?.Display ?? Driver;

    public string DriverNote => NoteFor(_draft, Driver);

    public bool CanChangeDriver => _target is null || _target.Devices.Count == 0;

    public static string NoteFor(ConfigDraft draft, string driver)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return "Hỗ trợ dòng PLC: " + string.Join(", ", draft.Models.Where(m => m.Driver == driver).Select(m => m.Name));
    }

    /// <summary>The driver dialog's choice; refused when the channel has PLCs or the driver is unknown.</summary>
    public bool PickDriver(string driver)
    {
        if (!CanChangeDriver || Drivers.All(d => d.Driver != driver))
        {
            return false;
        }

        Driver = driver;
        return true;
    }

    public bool Apply()
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            SetErrors(new Dictionary<string, string> { ["Name"] = "không được để trống" });
            return false;
        }

        SetErrors([]);
        var edited = new DraftChannel { Name = name, Driver = Driver };
        if (_target is null)
        {
            _draft.AddChannel(edited);
        }
        else
        {
            _draft.Update(_target, edited);
        }

        return true;
    }
}
