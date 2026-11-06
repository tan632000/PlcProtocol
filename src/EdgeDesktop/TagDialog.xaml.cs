using System.Windows;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.EdgeDesktop;

/// <summary>Add or edit a tag; in Add mode Áp dụng keeps the dialog open for the next tag, unless its PLC is gone.</summary>
public partial class TagDialog : Window
{
    private readonly DialogRequest _request;
    private readonly TagDialogViewModel _tag;

    public TagDialog(DialogRequest request)
    {
        _request = request;
        _tag = (TagDialogViewModel)request.ViewModel;
        InitializeComponent();
        DataContext = _tag;
        Title = _tag.IsAddMode ? "Thêm Tag" : "Sửa Tag";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!_request.Ok())
        {
            return;
        }

        if (_tag.CloseAfterApply || _tag.IsDetached)
        {
            DialogResult = true;
            return;
        }

        NameBox.Focus(); // the next tag
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        try
        {
            await _request.Check!();
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }
}
