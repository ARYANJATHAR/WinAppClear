using System.ComponentModel;
using System.Windows;
using WinAppClear.App.ViewModels;

namespace WinAppClear.App.Views;

public partial class UninstallWindow : Window
{
    private readonly UninstallViewModel _vm;

    public UninstallWindow(UninstallViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Owner = Application.Current.MainWindow;
        vm.RequestClose += Close;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Don't let the window close in the middle of an uninstall or removal.
        if (_vm.IsRunning)
        {
            e.Cancel = true;
            return;
        }
        // Closing during review = keep everything (still record history).
        if (_vm.IsReview) _vm.SkipRemovalCommand.Execute(null);
        base.OnClosing(e);
    }
}
