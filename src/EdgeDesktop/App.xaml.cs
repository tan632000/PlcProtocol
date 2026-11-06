using System.Windows;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.EdgeDesktop;

public partial class App : Application
{
    /// <summary>Exit code for an invalid command line.</summary>
    private const int InvalidArguments = 2;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!LaunchOptions.TryParse(e.Args, out var options, out var error))
        {
            MessageBox.Show(error, "IIoT Edge Monitor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(InvalidArguments);
            return;
        }

        var settings = new SettingsClient(options!.Port);
        var prompts = new WpfPrompts();
        var main = new MainViewModel(new MonitorViewModel(TimeProvider.System), settings, prompts, prompts, TimeProvider.System, options.Port);
        var window = new MainWindow(options, main);
        window.Closed += (_, _) => settings.Dispose();
        window.Show();
    }
}
