using System.Windows;
using System.Windows.Threading;
using WinAppClear.App.Infrastructure;
using WinAppClear.App.Views;
using WinAppClear.Core.Cleanup;
using WinAppClear.Core.Util;

namespace WinAppClear.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Isolate developer startup checks from the user's normal backups/settings.
        var dataDirIndex = Array.FindIndex(e.Args, a => a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));
        if (dataDirIndex >= 0 && dataDirIndex + 1 < e.Args.Length)
            AppPaths.OverrideRoot(Path.GetFullPath(e.Args[dataDirIndex + 1]));
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task", args.Exception);
            args.SetObserved();
        };

        Log.Info($"WinAppClear {typeof(App).Assembly.GetName().Version} starting");
        _ = Task.Run(() => BackupMaintenance.PruneOld(AppSettings.Current.BackupRetentionDays));

        MainWindow = new MainWindow();
        MainWindow.Show();
        HandleDeveloperArguments(e.Args);
    }

    /// <summary>
    /// Developer switches for checking screens without clicking through them:
    /// --page "Software Health"  opens a page;  --snapshot out.png [--delay 8000]  saves the window as a PNG and exits.
    /// </summary>
    private void HandleDeveloperArguments(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Arg("--page") is { } page) ViewModels.MainViewModel.Instance?.NavigateTo(page);
        if (Arg("--snapshot") is not { } file) return;

        var delay = int.TryParse(Arg("--delay"), out var d) ? d : 6000;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var root = (FrameworkElement)MainWindow!.Content;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(file);
                encoder.Save(fs);
            }
            catch (Exception ex)
            {
                Log.Error("Snapshot", ex);
            }
            Shutdown();
        };
        timer.Start();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        Dialogs.Error($"Something unexpected happened, but WinAppClear is still running.\n\n{e.Exception.Message}\n\nDetails were written to the log.");
        e.Handled = true;
    }
}
