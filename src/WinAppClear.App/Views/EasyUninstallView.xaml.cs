using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinAppClear.App.ViewModels;

namespace WinAppClear.App.Views;

public partial class EasyUninstallView : UserControl
{
    private bool _dragging;

    public EasyUninstallView()
    {
        InitializeComponent();
    }

    private void Target_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        // Mouse capture keeps the button-up event coming here even when the pointer is over another window.
        Target.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Cross;
        TargetGlyph.Opacity = 0.25;
        e.Handled = true;
    }

    private void Target_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Target.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        TargetGlyph.Opacity = 1;
        if (DataContext is EasyUninstallViewModel vm) vm.IdentifyAtCursor();
        e.Handled = true;
    }
}
