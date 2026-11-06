using System.Windows;

namespace IndustrialIot.EdgeDesktop;

/// <summary>
/// The settings password for one save or one connection test (plan D-06). OK is disabled while the box is empty; the
/// value is read once for the caller's one request and the box is cleared, so no field of the app keeps it.
/// </summary>
public partial class PasswordDialog : Window
{
    private PasswordDialog(string purpose)
    {
        InitializeComponent();
        Purpose.Text = purpose;
    }

    /// <summary>The typed password, or null when cancelled.</summary>
    public static string? Ask(Window? owner, string purpose)
    {
        var dialog = new PasswordDialog(purpose) { Owner = owner };
        var typed = dialog.ShowDialog() == true ? dialog.Box.Password : null;
        dialog.Box.Clear();
        return typed;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        // The length of a SecurePassword copy, so no managed string of the password is made on each key.
        using var secure = Box.SecurePassword;
        OkButton.IsEnabled = secure.Length > 0;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
