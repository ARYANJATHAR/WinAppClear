using System.Collections.ObjectModel;
using System.Diagnostics;
using WinAppClear.App.Infrastructure;
using WinAppClear.App.Views;
using WinAppClear.Core.Cleanup;
using WinAppClear.Core.Health;
using WinAppClear.Core.Inventory;
using WinAppClear.Core.Models;
using WinAppClear.Core.Safety;
using WinAppClear.Core.Uninstall;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinAppClear.App.ViewModels;

// ---------------- Force Uninstall ----------------

public partial class ForceUninstallViewModel : PageViewModel
{
    [ObservableProperty] private string? _folder;
    [ObservableProperty] private string _programName = "";

    public ObservableCollection<InstalledProgram> BrokenPrograms { get; } = [];
    public override string Title => "Force Uninstall";
    public override string Subtitle =>
        "For programs that won't uninstall normally, or folders a program left behind. Review detected leftovers and choose which items to remove.";

    protected override Task LoadAsync() => LoadBroken();

    [RelayCommand]
    private Task Refresh() => RunBusy("Looking for broken programs…", LoadBroken);

    private async Task LoadBroken()
    {
        var issues = await new UninstallIssuesCheck().ScanAsync(new HealthContext(), CancellationToken.None);
        BrokenPrograms.Clear();
        foreach (var p in issues.Select(i => i.Payload).OfType<InstalledProgram>()) BrokenPrograms.Add(p);
    }

    [RelayCommand]
    private void Browse()
    {
        var folder = Dialogs.PickFolder("Choose the program's folder");
        if (folder is null) return;
        var reason = SafetyGuard.ProtectedPathReason(folder);
        if (reason is not null)
        {
            Dialogs.Info($"WinAppClear won't force-uninstall this folder ({reason}). Choose the program's own folder instead.");
            return;
        }
        Folder = folder;
        ProgramName = Path.GetFileName(folder);
    }

    [RelayCommand]
    private void ForceFolder()
    {
        if (Folder is null || !Directory.Exists(Folder))
        {
            Dialogs.Info("Choose the program's folder first.");
            return;
        }
        if (!CloseProcessesIn(Folder)) return;
        var program = ProgramInventory.Placeholder(string.IsNullOrWhiteSpace(ProgramName) ? Path.GetFileName(Folder) : ProgramName, Folder);
        var folder = Folder;
        var vm = new UninstallViewModel([program], forceMode: true)
        {
            CustomScan = (p, ct) => UninstallWorkflow.ScanFolderAsync(folder, p.DisplayName, ct),
        };
        new UninstallWindow(vm).ShowDialog();
        Folder = null;
        ProgramName = "";
    }

    [RelayCommand]
    private async Task ForceProgram(InstalledProgram? program)
    {
        if (program is null) return;
        if (ProgramInventory.GuessInstallFolder(program) is { } folder && Directory.Exists(folder) && !CloseProcessesIn(folder)) return;
        new UninstallWindow(new UninstallViewModel([program], forceMode: true)).ShowDialog();
        await RunBusy("Refreshing…", LoadBroken);
    }

    /// <summary>Closes programs running from the folder (after asking). Returns false if the user backs out.</summary>
    internal static bool CloseProcessesIn(string folder)
    {
        var running = ProcessService.FindUnder(folder);
        if (running.Count == 0) return true;
        var names = string.Join(", ", running.Select(r => r.Name).Distinct());
        if (!Dialogs.Confirm($"These programs are running from that folder and must be closed first:\n\n{names}\n\nClose them now? Unsaved work in them will be lost.", warning: true))
            return false;
        foreach (var p in running) ProcessService.Kill(p.Id);
        return true;
    }
}

// ---------------- Easy Uninstall ----------------

public sealed record WindowChoice(int Pid, string Title, string? Path)
{
    public override string ToString() => $"{Title}  ({System.IO.Path.GetFileName(Path)})";
}

public partial class EasyUninstallViewModel : PageViewModel
{
    [ObservableProperty] private string? _targetPath;
    [ObservableProperty] private InstalledProgram? _match;
    [ObservableProperty] private string? _message = "Drag the target onto any open window, or pick a window from the list.";
    [ObservableProperty] private WindowChoice? _selectedWindow;

    public ObservableCollection<WindowChoice> Windows { get; } = [];
    public override string Title => "Easy Uninstall";
    public override string Subtitle => "Don't know a program's name? Drag the target onto its window and WinAppClear finds it for you.";

    protected override Task LoadAsync() => RefreshWindows();

    [RelayCommand]
    private Task RefreshWindows()
    {
        Windows.Clear();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle) || p.Id == Environment.ProcessId) continue;
                    Windows.Add(new WindowChoice(p.Id, p.MainWindowTitle, ProcessService.GetImagePath(p.Id)));
                }
                catch
                {
                    // Process exited.
                }
            }
        }
        return Task.CompletedTask;
    }

    partial void OnSelectedWindowChanged(WindowChoice? value)
    {
        if (value is not null) Identify(value.Path);
    }

    /// <summary>Called by the view when the crosshair is released over a window.</summary>
    public void IdentifyAtCursor()
    {
        var (x, y) = ProcessService.CursorPosition();
        var (pid, path) = ProcessService.ProcessAtScreenPoint(x, y);
        if (pid == Environment.ProcessId)
        {
            Message = "That's WinAppClear itself — drop the target on another program's window.";
            return;
        }
        Identify(path);
    }

    private void Identify(string? path)
    {
        TargetPath = path;
        if (path is null)
        {
            Match = null;
            Message = "Couldn't read which program owns that window (it may be running as administrator or be part of Windows).";
            return;
        }
        if (PathUtil.IsUnderOrEqual(path, KnownPaths.Windows))
        {
            Match = null;
            Message = "That window belongs to Windows itself and can't be uninstalled.";
            return;
        }
        Match = ProgramLookup.FindByExecutable(ProgramInventory.GetInstalledPrograms(), path);
        Message = Match is null
            ? "No installed program matches this file. It may be portable — use Force Uninstall on its folder instead."
            : null;
    }

    [RelayCommand]
    private void Uninstall()
    {
        if (Match is null) return;
        new UninstallWindow(new UninstallViewModel([Match])).ShowDialog();
        Match = null;
        TargetPath = null;
        Message = "Drag the target onto any open window, or pick a window from the list.";
    }

    [RelayCommand]
    private void ForceUninstallFolder()
    {
        if (TargetPath is null) return;
        var folder = Path.GetDirectoryName(TargetPath);
        if (folder is null || SafetyGuard.IsProtectedPath(folder))
        {
            Dialogs.Info("That program's folder is protected.");
            return;
        }
        if (!ForceUninstallViewModel.CloseProcessesIn(folder)) return;
        var program = ProgramInventory.Placeholder(Path.GetFileName(folder), folder);
        new UninstallWindow(new UninstallViewModel([program], forceMode: true)
        {
            CustomScan = (p, ct) => UninstallWorkflow.ScanFolderAsync(folder, p.DisplayName, ct),
        }).ShowDialog();
    }
}

// ---------------- Force Delete ----------------

public sealed record PathResult(string Path, string Result, string Tone);

public partial class ForceDeleteViewModel : PageViewModel
{
    [ObservableProperty] private bool _closeLockingPrograms = true;
    [ObservableProperty] private bool _useRecycleBin;

    public ObservableCollection<string> Paths { get; } = [];
    public ObservableCollection<string> Lockers { get; } = [];
    public ObservableCollection<PathResult> Results { get; } = [];
    public override string Title => "Force Delete";
    public override string Subtitle =>
        "Delete files and folders Windows says are \"in use\". WinAppClear finds what's holding them, closes it, and schedules anything still stuck for removal at restart. No driver needed.";

    [RelayCommand]
    private void AddFiles()
    {
        foreach (var f in Dialogs.PickFiles("Choose files to delete")) AddPath(f);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var f = Dialogs.PickFolder("Choose a folder to delete");
        if (f is not null) AddPath(f);
    }

    public void AddPath(string path)
    {
        if (!Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) Paths.Add(path);
    }

    [RelayCommand]
    private void RemovePath(string? path)
    {
        if (path is not null) Paths.Remove(path);
    }

    [RelayCommand]
    private Task FindLockers() => RunBusy("Checking which programs are using these files…", async () =>
    {
        var lockers = await Task.Run(() => FileLockFinder.WhoIsLocking(FileLockFinder.ExpandFiles(Paths)));
        Lockers.Clear();
        foreach (var l in lockers) Lockers.Add($"{l.Name} (PID {l.Pid}) — {l.Path}");
        if (lockers.Count == 0) Lockers.Add("Nothing is using these files right now.");
    });

    [RelayCommand]
    private async Task Delete()
    {
        if (Paths.Count == 0)
        {
            Dialogs.Info("Add files or folders first.");
            return;
        }
        var how = UseRecycleBin ? "moved to the Recycle Bin" : "permanently deleted (no backup)";
        if (!Dialogs.Confirm($"{Paths.Count} item(s) will be {how}. Continue?", warning: !UseRecycleBin)) return;

        await RunBusy("Deleting…", async () =>
        {
            Results.Clear();
            foreach (var p in Paths.ToList())
            {
                var r = await Task.Run(() => ForceDeleter.Delete(p, CloseLockingPrograms, UseRecycleBin));
                var (text, tone) = r.Deleted ? (r.Message ?? "Deleted", "Accent")
                    : r.ScheduledForReboot ? (r.Message ?? "Will be deleted at restart", "Warning")
                    : (r.Message ?? "Failed", "Danger");
                Results.Add(new PathResult(p, text, tone));
                if (r.Deleted || r.ScheduledForReboot) Paths.Remove(p);
            }
            Lockers.Clear();
        });
    }
}

// ---------------- File Shredder ----------------

public partial class ShredderViewModel : PageViewModel
{
    [ObservableProperty] private int _passes = AppSettings.Current.ShredPasses;

    public int[] PassOptions { get; } = [1, 3, 7];
    public ObservableCollection<string> Paths { get; } = [];
    public ObservableCollection<PathResult> Results { get; } = [];
    public override string Title => "File Shredder";
    public override string Subtitle =>
        "Overwrite files with random data before deleting them, so recovery tools can't bring them back. On SSDs this is best-effort — use BitLocker for full protection.";

    [RelayCommand]
    private void AddFiles()
    {
        foreach (var f in Dialogs.PickFiles("Choose files to shred")) AddPath(f);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var f = Dialogs.PickFolder("Choose a folder to shred");
        if (f is not null) AddPath(f);
    }

    public void AddPath(string path)
    {
        if (!Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) Paths.Add(path);
    }

    [RelayCommand]
    private void RemovePath(string? path)
    {
        if (path is not null) Paths.Remove(path);
    }

    [RelayCommand]
    private async Task Shred()
    {
        if (Paths.Count == 0)
        {
            Dialogs.Info("Add files or folders first.");
            return;
        }
        if (!Dialogs.Confirm($"Shred {Paths.Count} item(s)? This CANNOT be undone — there is no backup and no Recycle Bin.", warning: true))
            return;

        AppSettings.Current.ShredPasses = Passes;
        AppSettings.Current.Save();
        await RunBusy("Shredding…", async () =>
        {
            Results.Clear();
            var results = await Task.Run(() => FileShredder.Shred(Paths.ToList(), Passes, BusyProgress));
            var failed = results.Where(r => !r.Success).ToList();
            foreach (var f in failed.Take(50)) Results.Add(new PathResult(f.Path, f.Message ?? "Failed", "Danger"));
            Results.Insert(0, new PathResult($"{results.Count(r => r.Success)} file(s) shredded", failed.Count == 0 ? "Done" : $"{failed.Count} failed", failed.Count == 0 ? "Accent" : "Warning"));
            foreach (var p in Paths.ToList())
                if (!File.Exists(p) && !Directory.Exists(p)) Paths.Remove(p);
        });
    }
}
