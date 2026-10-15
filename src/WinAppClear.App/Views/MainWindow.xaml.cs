using System.Windows;

namespace WinAppClear.App.Views;

public partial class MainWindow : Window
{
    private void ShowSectionHelp(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ToolTip: System.Windows.Controls.ToolTip tip } button)
        {
            tip.PlacementTarget = button;
            tip.IsOpen = !tip.IsOpen;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
    }
}
