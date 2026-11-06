using System.Windows;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.EdgeDesktop;

/// <summary>Add or edit a PLC, with Test kết nối on the dialog's current values.</summary>
public partial class DeviceDialog : Window
{
    private readonly DialogRequest _request;

    public DeviceDialog(DialogRequest request)
    {
        _request = request;
        InitializeComponent();
        DataContext = request.ViewModel;
        Title = ((DeviceDialogViewModel)request.ViewModel).IsAddMode ? "Thêm PLC" : "Sửa PLC";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_request.Ok())
        {
            DialogResult = true;
        }
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        // One test at a time from this dialog: disabled until the Edge has answered.
        TestButton.IsEnabled = false;
        try
        {
            await _request.TestConnection!();
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }
}
