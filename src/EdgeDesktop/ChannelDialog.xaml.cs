using System.Windows;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.EdgeDesktop;

/// <summary>Add or edit a channel: its OK runs <see cref="DialogRequest.Ok"/>, which keeps the dialog open on an error.</summary>
public partial class ChannelDialog : Window
{
    private readonly DialogRequest _request;

    public ChannelDialog(DialogRequest request)
    {
        _request = request;
        InitializeComponent();
        DataContext = request.ViewModel;
        Title = ((ChannelDialogViewModel)request.ViewModel).IsAddMode ? "Thêm Channel" : "Sửa Channel";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_request.Ok())
        {
            DialogResult = true;
        }
    }

    private void OnDriver(object sender, RoutedEventArgs e) =>
        new DriverDialog((ChannelDialogViewModel)DataContext) { Owner = this }.ShowDialog();
}
