using System.Windows;
using System.Windows.Controls;
using WinAppClear.App.ViewModels;

namespace WinAppClear.App.Views;

public partial class ForceDeleteView : UserControl
{
    public ForceDeleteView()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && DataContext is ForceDeleteViewModel vm)
            foreach (var p in paths) vm.AddPath(p);
    }
}
