using System.Windows;
using System.Windows.Controls;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.EdgeDesktop;

/// <summary>
/// The view models' questions and dialogs as modal windows, each owned by the window in front: the password dialog,
/// the add/edit dialogs, and a small choice window whose buttons carry the plan's own words (Lưu / Không lưu / Huỷ…).
/// </summary>
public sealed class WpfPrompts : IUserPrompts, IDialogOpener
{
    private const string Caption = "IIoT Edge Monitor";

    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public Task<string?> AskPasswordAsync(string purpose) => Task.FromResult(PasswordDialog.Ask(Owner, purpose));

    public Task<bool> ConfirmAsync(string message) => Task.FromResult(Choose(message, cancel: 1, enter: 0, "Đồng ý", "Huỷ") == 0);

    // Enter keeps the draft: one key press must not drop every edit.
    public Task<bool> AskReloadStaleAsync(string message) =>
        Task.FromResult(Choose(message, cancel: 1, enter: 1, "Tải lại (bỏ thay đổi)", "Giữ để xem") == 0);

    public Task<CloseChoice> AskCloseAsync() =>
        Task.FromResult(Choose("Có thay đổi chưa lưu. Lưu & áp dụng trước khi thoát?", cancel: 2, enter: 0, "Lưu", "Không lưu", "Huỷ") switch
        {
            0 => CloseChoice.Save,
            1 => CloseChoice.DontSave,
            _ => CloseChoice.Cancel,
        });

    public Task<bool> AskExitWhileBusyAsync() =>
        Task.FromResult(Choose(
            "Đang lưu hoặc tải cấu hình. Thoát ngay thì Edge vẫn tự áp dụng xong lần lưu, nhưng app không báo kết quả.",
            cancel: 0, enter: 0, "Đợi", "Thoát không chờ") == 1);

    public Task OpenAsync(DialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Window dialog = request.ViewModel switch
        {
            ChannelDialogViewModel => new ChannelDialog(request),
            DeviceDialogViewModel => new DeviceDialog(request),
            TagDialogViewModel => new TagDialog(request),
            _ => throw new ArgumentException($"No dialog for {request.ViewModel.GetType().Name}", nameof(request)),
        };
        dialog.Owner = Owner;
        dialog.ShowDialog();
        return Task.CompletedTask;
    }

    /// <summary>The index of the button clicked; Esc or the close box is <paramref name="cancel"/>, Enter <paramref name="enter"/>.</summary>
    private static int Choose(string message, int cancel, int enter, params string[] choices)
    {
        var chosen = cancel;
        var window = new Window
        {
            Title = Caption,
            Owner = Owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 12,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = choices[i],
                MinWidth = 80,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = i == enter,
                IsCancel = i == cancel,
            };
            button.Click += (_, _) =>
            {
                chosen = index;
                window.DialogResult = true;
            };
            buttons.Children.Add(button);
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            MaxWidth = 460,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons },
        };
        window.ShowDialog();
        return chosen;
    }
}
