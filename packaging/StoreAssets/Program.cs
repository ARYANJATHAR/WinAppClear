using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WinAppClear.App.ViewModels;
using WinAppClear.App.Views;
using WinAppClear.Core.Models;
using WinAppClear.Core.Util;
using WinAppClear.Core.Monitor;
using WinAppClear.Core.Cleanup;
using WinAppClear.Core.History;

internal static class Program
{
    private static string output = "";
    [STAThread]
    private static int Main(string[] args)
    {
        output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        SaveStoreLogo();
        AppPaths.OverrideRoot(Path.Combine(AppContext.BaseDirectory, "demo-data"));
        var app = new WinAppClear.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        int step = 0, exit = 1; UninstallWindow? wizard = null; UninstallViewModel? uninstall = null;
        var started = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(45)) throw new TimeoutException("Capture timed out");
                if (app.MainWindow is not MainWindow window || window.DataContext is not MainViewModel vm || vm.Programs.IsBusy) return;
                if (step == 0)
                {
                    window.Width = 1536; window.Height = 960;
                    typeof(MainViewModel).GetField("<IsAdmin>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, true);
                    window.DataContext = null; window.DataContext = vm;
                    vm.Programs.Rows.Clear();
                    foreach (var (name,size) in new[] {("Nebula Editor",245L),("Aurora Player",173L),("Orbit Notes",86L),("Lumen Studio",6L),("Atlas Reader",276L),("Pixel Desk",335L),("Comet Tools",389L)})
                        vm.Programs.Rows.Add(new ProgramRow(Demo(name,size)));
                    vm.Programs.Summary = "7 programs · 1.5 GB"; vm.SetBadge("All Programs", "7");
                    SeedRecords();
                }
                else if (step == 1)
                {
                    Save(window, "programs.png");
                    if (args.Contains("--search-check"))
                    {
                        vm.Programs.Search = "no";
                        Save(window, "search-no.png");
                        vm.Programs.Search = "zz-no-match";
                        Save(window, "search-no-match.png");
                        vm.Programs.Search = "  NEBULA  ";
                        Save(window, "search-match.png");
                        vm.Programs.Search = "";
                        exit=0; timer.Stop(); app.Shutdown();
                    }
                }
                else if (step == 2)
                {
                    uninstall = new UninstallViewModel([Demo("Nebula Editor",245)]) { Stage = UninstallStage.Review, SelectionSummary = "3 of 5 selected · 18.4 MB" };
                    var entries = new[]
                    {
                        Item(LeftoverKind.Folder,@"C:\Demo\Nebula Editor",Confidence.High,"Program install folder",12_000_000),
                        Item(LeftoverKind.Folder,@"C:\Users\Demo\AppData\Roaming\Nebula",Confidence.High,"Folder named after the program",6_400_000),
                        Item(LeftoverKind.RegistryKey,@"HKCU\Software\NebulaEditor",Confidence.High,"Registry key named after the program",0),
                        Item(LeftoverKind.Folder,@"C:\ProgramData\Nebula Labs",Confidence.Medium,"Publisher folder: review before removal",0),
                        Item(LeftoverKind.Folder,@"C:\Users\Demo\Documents\Nebula Projects",Confidence.Low,"May contain your own project files",0),
                    };
                    foreach (var item in entries) uninstall.Leftovers.Add(new LeftoverRow(item,"Nebula Editor",item.Confidence==Confidence.High));
                    wizard = new UninstallWindow(uninstall); wizard.Width=1060; wizard.Height=730; wizard.Show();
                }
                else if (step == 3) Save(wizard!, "review.png");
                else if (step == 4)
                {
                    uninstall!.Stage=UninstallStage.Done; wizard!.Close();
                    vm.NavigateTo("Install Monitor");
                    var monitor = (MonitorViewModel)vm.CurrentPage!;
                    monitor.SelectedLog = monitor.Logs.First();
                }
                else if (step == 5) Save(window, "monitor.png");
                else if (step == 6) vm.NavigateTo("History & Backups");
                else if (step == 7)
                {
                    if (vm.CurrentPage!.IsBusy) return;
                    Save(window,"history.png"); exit=0; timer.Stop(); app.Shutdown();
                }
                step++;
            }
            catch(Exception ex) { Console.Error.WriteLine(ex); timer.Stop(); app.Shutdown(); }
        };
        timer.Start(); app.Run(); return exit;
    }
    private static void SaveStoreLogo()
    {
        var source = new BitmapImage(new Uri(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../../src/WinAppClear.App/Assets/winappclear-512.png"))));
        var drawing = new DrawingVisual(); using(var dc=drawing.RenderOpen()) dc.DrawImage(source,new Rect(0,0,300,300));
        var bitmap=new RenderTargetBitmap(300,300,96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"logo-300.png"));png.Save(file);
    }
    private static InstalledProgram Demo(string name,long size) => new()
    {
        Id="demo-"+name, DisplayName=name,Publisher=name=="Nebula Editor"?"Nebula Labs":"Demo Software",DisplayVersion="1.4.2",
        EstimatedSizeBytes=size*1024*1024,InstallDate=new DateTime(2026,10,10),LastUsed=DateTime.Today.AddDays(-2),
        RegistryKeyPath=@"HKCU\Software\WinAppClear-Demo",KeyName="demo",Hive=RegistryHive.LocalMachine,View=RegistryView.Registry64,
    };
    private static LeftoverItem Item(LeftoverKind kind,string path,Confidence confidence,string reason,long size) => new()
    {Kind=kind,Path=path,Confidence=confidence,Reason=reason,SizeBytes=size};
    private static void SeedRecords()
    {
        InstallLogStore.Save(new InstallLog
        {
            Id="demo-install",Name="Nebula Editor",Started=new DateTime(2026,10,10,10,0,0),Finished=new DateTime(2026,10,10,10,2,0),
            NewProgramNames=["Nebula Editor"],AddedFolders=[@"C:\Demo\Nebula Editor",@"C:\Users\Demo\AppData\Roaming\Nebula"],
            AddedFiles=[@"C:\Users\Demo\Desktop\Nebula Editor.lnk"],AddedKeys=[@"64|HKCU\Software\NebulaEditor"],
        });
        var backup=BackupService.Begin("Nebula Editor leftovers");
        var file=Path.Combine(backup.Folder,"demo-settings.json"); File.WriteAllText(file,"{\"demo\":true}");
        backup.Entries.Add(new BackupEntry {Kind=LeftoverKind.File,OriginalPath=@"C:\Users\Demo\AppData\Roaming\Nebula\settings.json",BackupFile=file}); BackupService.Save(backup);
        HistoryStore.Add(new HistoryEntry {Name="Nebula Editor",Publisher="Nebula Labs",Version="1.4.2",Method=RemovalMethod.Standard,Result="Uninstalled. Demo leftovers backed up.",LeftoversFound=5,LeftoversRemoved=3,BytesFreed=18_400_000,BackupId=backup.Id});
    }
    private static void Save(Window window,string name)
    {
        window.UpdateLayout(); var content=(FrameworkElement)window.Content;
        var width=(int)content.ActualWidth;var height=(int)content.ActualHeight;
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {var canvas=new Rect(0,0,1536,960);dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(243,245,248)),null,canvas);var box=new Rect((1536-width)/2.0,(960-height)/2.0,width,height);dc.DrawRectangle(window.Background,null,box);dc.DrawRectangle(new VisualBrush(content),null,box);var label=new FormattedText("WinAppClear · Sample data",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),13,new SolidColorBrush(Color.FromRgb(83,103,123)),1);dc.DrawText(label,new Point(18,939));}
        var bitmap=new RenderTargetBitmap(1536,960,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name));png.Save(file);
        Console.WriteLine($"Saved {name} ({width}x{height})");
    }
}
