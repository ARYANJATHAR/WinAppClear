using System.Collections.ObjectModel;
using WinAppClear.App.Infrastructure;
using WinAppClear.App.Views;
using WinAppClear.Core.Inventory;
using WinAppClear.Core.Models;
using WinAppClear.Core.Monitor;
using WinAppClear.Core.Uninstall;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinAppClear.App.ViewModels;

public partial class MonitorViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _installName = "";
    [ObservableProperty] private string? _installerPath;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _recordingSince;
    [ObservableProperty] private InstallLog? _selectedLog;

    public ObservableCollection<InstallLog> Logs { get; } = [];
    public override string Title => "Install Monitor";
    public override string Subtitle =>
        "Record everything a program's installer adds — files, registry entries, services and tasks — so you can remove all of it later, perfectly.";

    public ObservableCollection<string> SelectedDetails { get; } = [];

    protected override Task LoadAsync()
    {
        RefreshState();
        return Task.CompletedTask;
    }

    private void RefreshState()
    {
        var pending = InstallMonitor.Pending;
        IsRecording = pending is not null;
        RecordingSince = pending is null ? null : $"Recording \"{pending.Name}\" since {pending.Started:t}";
        Logs.Clear();
        foreach (var l in InstallLogStore.All()) Logs.Add(l);
        MainViewModel.Instance?.SetBadge("Install Monitor", IsRecording ? "REC" : null);
    }

    partial void OnSelectedLogChanged(InstallLog? value)
    {
        SelectedDetails.Clear();
        if (value is null) return;
        void Section(string title, IEnumerable<string> items, Func<string, string>? format = null)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            SelectedDetails.Add($"— {title} ({list.Count}) —");
            foreach (var i in list.Take(200)) SelectedDetails.Add("   " + (format?.Invoke(i) ?? i));
            if (list.Count > 200) SelectedDetails.Add($"   … and {list.Count - 200} more");
        }
        static string Reg(string k) => k[(k.IndexOf('|') + 1)..].Replace('\0', '→');
        Section("Programs registered", value.NewProgramNames);
        Section("Services", value.AddedServices);
        Section("Scheduled tasks", value.AddedTasks);
        Section("Folders", value.AddedFolders);
        Section("Files", value.AddedFiles);
        Section("Registry keys", value.AddedKeys, Reg);
        Section("Registry values", value.AddedValues, Reg);
        Section("Changed files (not undone automatically)", value.ModifiedFiles);
    }

    [RelayCommand]
    private void BrowseInstaller()
    {
        var file = Dialogs.PickFile("Installers|*.exe;*.msi|All files|*.*", "Choose the setup file to monitor");
        if (file is null) return;
        InstallerPath = file;
        if (string.IsNullOrWhiteSpace(InstallName)) InstallName = Path.GetFileNameWithoutExtension(file);
    }

    [RelayCommand]
    private async Task Start()
    {
        var name = string.IsNullOrWhiteSpace(InstallName) ? "Unnamed install" : InstallName.Trim();
        _cts = new CancellationTokenSource();
        await RunBusy("Taking a snapshot of your system (about a minute)…", async () =>
        {
            await InstallMonitor.StartAsync(name, InstallerPath, BusyProgress, _cts.Token);
            RefreshState();
        });
        if (!IsRecording) return;

        if (InstallerPath is not null && File.Exists(InstallerPath))
        {
            await RunBusy("Waiting for the installer to finish… Complete the setup in its window.", async () =>
            {
                await InstallMonitor.RunInstallerAsync(InstallerPath, _cts.Token);
            });
            if (Dialogs.Confirm("The installer has closed. Finish recording now?\n\n" +
                                "Choose No if the program still needs to finish setting itself up (for example, on first launch)."))
                await Finish();
        }
        else
        {
            Dialogs.Info("Recording started. Now install the program as usual, then come back and click \"Finish recording\".");
        }
    }

    [RelayCommand]
    private async Task Finish()
    {
        _cts = new CancellationTokenSource();
        InstallLog? log = null;
        await RunBusy("Taking the second snapshot and comparing…", async () =>
        {
            log = await InstallMonitor.FinishAsync(BusyProgress, _cts.Token);
        });
        RefreshState();
        if (log is not null)
        {
            SelectedLog = Logs.FirstOrDefault(l => l.Id == log.Id);
            InstallName = "";
            InstallerPath = null;
            Dialogs.Info($"Recorded \"{log.Name}\": {log.AddedFolders.Count} folder(s), {log.AddedFiles.Count} file(s), " +
                         $"{log.AddedKeys.Count + log.AddedValues.Count} registry item(s), {log.AddedServices.Count} service(s), " +
                         $"{log.AddedTasks.Count} task(s).");
        }
    }

    [RelayCommand]
    private void CancelRecording()
    {
        if (!Dialogs.Confirm("Stop recording and discard the snapshot?")) return;
        _cts?.Cancel();
        InstallMonitor.Cancel();
        RefreshState();
    }

    [RelayCommand]
    private void DeleteLog(InstallLog? log)
    {
        log ??= SelectedLog;
        if (log is null || !Dialogs.Confirm($"Delete the install log for \"{log.Name}\"? The program itself is not touched.")) return;
        InstallLogStore.Delete(log);
        RefreshState();
    }

    [RelayCommand]
    private void UninstallWithLog(InstallLog? log)
    {
        log ??= SelectedLog;
        if (log is null) return;
        var programs = InstallMonitor.ProgramsFromLog(log);
        var forceMode = programs.Count == 0;
        if (forceMode) programs = [ProgramInventory.Placeholder(log.Name, log.AddedFolders.FirstOrDefault())];

        var vm = new UninstallViewModel(programs, forceMode)
        {
            CustomScan = async (program, ct) =>
            {
                var fromLog = await Task.Run(() => InstallMonitor.ToLeftovers(log), ct);
                List<LeftoverItem> standard = forceMode ? [] : await UninstallWorkflow.ScanLeftoversAsync(program, ct);
                return fromLog.Concat(standard).DistinctBy(i => i.Key).ToList();
            },
        };
        new UninstallWindow(vm).ShowDialog();
        log.Removed = true;
        InstallLogStore.Save(log);
        RefreshState();
    }
}
