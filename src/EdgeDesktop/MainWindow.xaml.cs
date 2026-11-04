using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.EdgeDesktop;

/// <summary>The monitor window: binds <see cref="MonitorViewModel"/> and runs its poll loop while open.</summary>
public partial class MainWindow : Window
{
    private readonly LaunchOptions _options;
    private readonly MonitorViewModel _viewModel = new(TimeProvider.System);
    private readonly CancellationTokenSource _stop = new();
    private StatusClient? _client;

    public MainWindow(LaunchOptions options)
    {
        _options = options;
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded; // one poll loop per window, even if Loaded fires again
        _client = new StatusClient(_options.Port);
        // Every view-model change runs on this window's UI thread; RunAsync never throws.
        _ = _viewModel.RunAsync(_client, action => Dispatcher.BeginInvoke(action), _stop.Token);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Cancelled on the UI thread, so an action already posted sees it and changes nothing.
        _stop.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _client?.Dispose();
        _stop.Dispose();
        base.OnClosed(e);
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
                $"Không mở được trình duyệt. Hãy mở {_options.SettingsUri} bằng tay.",
                "IIoT Edge Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
