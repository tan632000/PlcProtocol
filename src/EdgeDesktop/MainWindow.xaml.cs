using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.EdgeDesktop;

/// <summary>
/// The main window: binds <see cref="MainViewModel"/>, runs the one poll loop while open, and routes every menu item,
/// toolbar button, key and double-click to the view model's commands, which decide what may run (busy included).
/// </summary>
public partial class MainWindow : Window
{
    public static readonly RoutedUICommand SaveCommand = Command("Lưu & áp dụng", new KeyGesture(Key.S, ModifierKeys.Control, "Ctrl+S"));
    public static readonly RoutedUICommand ReloadCommand = Command("Tải lại từ Edge", new KeyGesture(Key.F5));
    public static readonly RoutedUICommand AddChannelCommand = Command("Thêm Channel");
    public static readonly RoutedUICommand AddDeviceCommand = Command("Thêm PLC");
    public static readonly RoutedUICommand AddTagCommand = Command("Thêm Tag");
    public static readonly RoutedUICommand EditCommand = Command("Sửa…");
    public static readonly RoutedUICommand DeleteCommand = Command("Xoá", new KeyGesture(Key.Delete, ModifierKeys.None, "Del"));
    public static readonly RoutedUICommand CheckCommand = Command("Kiểm tra cấu hình");
    public static readonly RoutedUICommand TestConnectionCommand = Command("Test kết nối PLC");

    private readonly LaunchOptions _options;
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _stop = new();
    private StatusClient? _client;
    private bool _mayClose;
    private bool _closed;
    private int _askingClose;

    public MainWindow(LaunchOptions options, MainViewModel viewModel)
    {
        _options = options;
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        Bind(SaveCommand, () => viewModel.CanSave, viewModel.SaveAsync);
        Bind(ReloadCommand, () => viewModel.CanReload, viewModel.ReloadAsync);
        Bind(AddChannelCommand, () => viewModel.CanAddChannel, viewModel.AddChannelAsync);
        Bind(AddDeviceCommand, () => viewModel.CanAddDevice, viewModel.AddDeviceAsync);
        Bind(AddTagCommand, () => viewModel.CanAddTag, viewModel.AddTagAsync);
        Bind(EditCommand, () => viewModel.CanEdit, viewModel.EditAsync);
        Bind(DeleteCommand, () => viewModel.CanDelete, viewModel.DeleteAsync);
        Bind(CheckCommand, () => viewModel.CanCheck, viewModel.CheckAsync);
        Bind(TestConnectionCommand, () => viewModel.CanTestConnection, viewModel.TestConnectionAsync);

        // The rules change with the poll, busy and the selection, not only with input: ask the buttons again.
        viewModel.PropertyChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded; // one poll loop per window, even if Loaded fires again
        _client = new StatusClient(_options.Port);
        // Every view-model change runs on this window's UI thread; RunAsync never throws.
        _ = _viewModel.Monitor.RunAsync(_client, action => Dispatcher.BeginInvoke(action), _stop.Token);
        _ = _viewModel.ReloadAsync(); // the first draft, busy while it is read
    }

    /// <summary>The close guard: Lưu / Không lưu / Huỷ with edits, Đợi / Thoát không chờ while busy.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_mayClose)
        {
            // Cancelled on the UI thread, so an action already posted sees it and changes nothing.
            _stop.Cancel();
            return;
        }

        e.Cancel = true;
        // Asked again while a save started by the first close runs, so Đợi / Thoát không chờ is offered.
        if (_askingClose == 0 || _viewModel.IsBusy)
        {
            _ = AskCloseAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _client?.Dispose();
        _stop.Dispose();
        base.OnClosed(e);
    }

    private static RoutedUICommand Command(string text, params InputGesture[] gestures) =>
        new(text, text, typeof(MainWindow), [.. gestures]);

    private async Task AskCloseAsync()
    {
        _askingClose++;
        bool close;
        try
        {
            close = await _viewModel.ConfirmCloseAsync();
        }
        finally
        {
            _askingClose--;
        }

        if (close && !_closed)
        {
            _mayClose = true;
            // Not from inside OnClosing, where the prompt may still be on the stack.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void Bind(RoutedUICommand command, Func<bool> canExecute, Func<Task> execute) =>
        CommandBindings.Add(new CommandBinding(
            command,
            async (_, _) => await execute(),
            (_, e) => e.CanExecute = canExecute()));

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.SelectedNode = e.NewValue as TreeNode;

    // Back in the tree, Sửa and Xoá act on its node, not on a tag still selected in the grid.
    private void OnTreeFocused(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.SelectedTag = null;

    private async void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The item under the mouse, not its parents, which get the same event.
        if (Ancestor<TreeViewItem>(e.OriginalSource) is { DataContext: TreeNode node })
        {
            e.Handled = true;
            await _viewModel.ActivateAsync(node);
        }
    }

    private async void OnTreeKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SelectedNode is { } node)
        {
            e.Handled = true;
            await _viewModel.ActivateAsync(node);
        }
    }

    private async void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<DataGridRow>(e.OriginalSource) is not null)
        {
            e.Handled = true;
            await _viewModel.EditAsync();
        }
    }

    private async void OnGridKey(object sender, KeyEventArgs e)
    {
        // Enter opens the tag instead of moving to the next row.
        if (e.Key == Key.Enter && _viewModel.SelectedTag is not null)
        {
            e.Handled = true;
            await _viewModel.EditAsync();
        }
    }

    private async void OnWindowKey(object sender, KeyEventArgs e)
    {
        // A rebuilt draft (after each dialog) replaces the grid's rows, which can take the keyboard focus out of the
        // grid; Enter then reaches the window and still opens the selected tag. The tree handles its own Enter first.
        if (e.Key == Key.Enter && _viewModel.SelectedTag is not null && !Tree.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            await _viewModel.EditAsync();
        }
    }

    private void OnLogShown(object sender, RoutedEventArgs e)
    {
        if (LogRow is null)
        {
            return; // IsChecked set while the XAML loads
        }

        var shown = LogMenu.IsChecked;
        LogRow.Height = shown ? new GridLength(180) : new GridLength(0);
        LogSplitterRow.Height = shown ? new GridLength(5) : new GridLength(0);
        LogGrid.Visibility = LogSplitter.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
        MessageBox.Show(this, $"IIoT Edge Monitor {version}\nEdge: {_viewModel.Endpoint}", "Giới thiệu", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_options.SettingsUri.ToString()) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            MessageBox.Show(
                this, $"Không mở được trình duyệt. Hãy mở {_options.SettingsUri} bằng tay.",
                "IIoT Edge Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static T? Ancestor<T>(object source)
        where T : DependencyObject
    {
        // A text element (a Run) is not a Visual: walk up its logical parents until the visual tree resumes.
        for (var at = source as DependencyObject; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is T found)
            {
                return found;
            }
        }

        return null;
    }
}
