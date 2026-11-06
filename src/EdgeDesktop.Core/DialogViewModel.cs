using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace IndustrialIot.EdgeDesktop.Core;

/// <summary>
/// What every add/edit dialog shares: change notification, the errors per field (local "phải là số" and the Edge's
/// own), and reading the number boxes. A dialog edits its own fields and touches the draft only in its Apply.
/// </summary>
public abstract class DialogViewModel : INotifyPropertyChanged
{
    public const string NotANumber = "phải là số";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Field name (as the Edge names it, e.g. <c>Port</c>, <c>Address</c>) → message.</summary>
    public IReadOnlyDictionary<string, string> FieldErrors { get; private set; } = new Dictionary<string, string>();

    /// <summary>Set when OK met a node no longer in the draft: nothing is left to edit, so the dialog closes in any mode.</summary>
    public bool IsDetached { get; internal set; }

    protected void SetErrors(Dictionary<string, string> errors)
    {
        FieldErrors = errors;
        OnPropertyChanged(nameof(FieldErrors));
    }

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(name);
        }
    }

    protected void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Digits only; empty is null when <paramref name="required"/> is false, an error otherwise.</summary>
    protected static int? Whole(string? text, string field, bool required, Dictionary<string, string> errors)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0 && !required)
        {
            return null;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors[field] = NotANumber;
        return null;
    }

    /// <summary>A decimal with a point (<c>1.5</c>; <c>1,5</c> is refused, as the web page refuses it); empty is null.</summary>
    protected static decimal? Fraction(string? text, string field, Dictionary<string, string> errors)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors[field] = NotANumber;
        return null;
    }

    protected static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    protected static string Text(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}
