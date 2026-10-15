using System.Windows;
using System.Windows.Controls;
using WinAppClear.App.ViewModels;

namespace WinAppClear.App.Views;

public partial class ShredderView : UserControl
{
    public ShredderView()
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
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && DataContext is ShredderViewModel vm)
            foreach (var p in paths) vm.AddPath(p);
    }
}
