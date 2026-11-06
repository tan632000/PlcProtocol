using System.Windows;
using System.Windows.Controls;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.EdgeDesktop;

/// <summary>Picks the channel dialog's driver: the note follows the choice at once, and Huỷ puts the driver back.</summary>
public partial class DriverDialog : Window
{
    private readonly ChannelDialogViewModel _channel;
    private readonly string _before;

    public DriverDialog(ChannelDialogViewModel channel)
    {
        _channel = channel;
        _before = channel.Driver;
        InitializeComponent();
        DataContext = channel;
        DriverBox.SelectedValue = channel.Driver;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
        {
            _channel.PickDriver(_before);
        }

        base.OnClosed(e);
    }

    private void OnPick(object sender, SelectionChangedEventArgs e)
    {
        if (DriverBox.SelectedValue is string driver)
        {
            _channel.PickDriver(driver);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
