using System.Collections.ObjectModel;
using WinAppClear.App.Infrastructure;
using WinAppClear.Core.Cleanup;
using WinAppClear.Core.Health;
using WinAppClear.Core.History;
using WinAppClear.Core.Models;
using WinAppClear.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinAppClear.App.ViewModels;

public sealed class BackupRow
{
    public BackupRow(BackupSession session)
    {
        Session = session;
        Size = BackupService.SizeOnDisk(session);
    }

    public BackupSession Session { get; }
    public string Title => Session.Title;
    public DateTime Created => Session.CreatedAt;
    public int Items => Session.Entries.Count;
    public long Size { get; }
    public string State => Session.Restored ? "Restored" : $"{Session.Entries.Count(e => e.Restorable)} restorable item(s)";
}

public partial class HistoryViewModel : PageViewModel
{
    [ObservableProperty] private HistoryEntry? _selectedEntry;

    public ObservableCollection<HistoryEntry> Entries { get; } = [];
    public ObservableCollection<BackupRow> Backups { get; } = [];
    public ObservableCollection<LeftoverItem> SelectedRemaining { get; } = [];
    public override string Title => "History & Backups";
    public override string Subtitle => "Everything WinAppClear removed, and the backups that let you undo it.";

    protected override Task LoadAsync() => Reload();

    [RelayCommand]
    private Task Refresh() => RunBusy("Loading…", Reload);

    private async Task Reload()
    {
        var (entries, backups) = await Task.Run(() => (HistoryStore.All(), BackupService.List().Select(b => new BackupRow(b)).ToList()));
        Entries.Clear();
        foreach (var e in entries) Entries.Add(e);
        Backups.Clear();
        foreach (var b in backups) Backups.Add(b);
        StatusText = $"{entries.Count} removal(s) · {backups.Count} backup(s) using {PathUtil.FormatSize(backups.Sum(b => b.Size))}";
    }

    partial void OnSelectedEntryChanged(HistoryEntry? value)
    {
        SelectedRemaining.Clear();
        if (value is null) return;
        foreach (var r in value.RemainingLeftovers) SelectedRemaining.Add(r);
    }

    [RelayCommand]
    private async Task Restore(BackupRow? row)
    {
        if (row is null) return;
        if (!Dialogs.Confirm($"Put back everything from \"{row.Title}\"?\n\nFiles, registry entries, services and tasks are restored to where they were.")) return;
        await RunBusy("Restoring…", async () =>
        {
            var report = await Task.Run(() => BackupService.Restore(row.Session));
            await Reload();
            var msg = $"{report.Restored} item(s) restored.";
            if (report.Failed > 0) msg += $" {report.Failed} could not be restored.";
            if (report.Messages.Count > 0) msg += "\n\n" + string.Join("\n", report.Messages.Take(10));
            Dialogs.Info(msg);
        });
    }

    [RelayCommand]
    private async Task DeleteBackup(BackupRow? row)
    {
        if (row is null || !Dialogs.Confirm($"Delete the backup \"{row.Title}\"? You won't be able to undo that removal anymore.")) return;
        await Task.Run(() => BackupService.Delete(row.Session));
        await Reload();
    }

    [RelayCommand]
    private void OpenBackupFolder(BackupRow? row)
    {
        if (row is not null) Dialogs.OpenInExplorer(row.Session.Folder);
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (!Dialogs.Confirm("Clear the uninstall history? Backups are kept.")) return;
        HistoryStore.Clear();
        await Reload();
    }
}

public partial class SettingsViewModel : PageViewModel
{
    private readonly AppSettings _s = AppSettings.Current;

    public override string Title => "Settings";
    public override string Subtitle => "How WinAppClear behaves. Changes are saved straight away.";

    public bool CreateRestorePoint
    {
        get => _s.CreateRestorePoint;
        set { _s.CreateRestorePoint = value; Save(); }
    }

    public bool QuietUninstallByDefault
    {
        get => _s.QuietUninstallByDefault;
        set { _s.QuietUninstallByDefault = value; Save(); }
    }

    public bool PreselectOnlyHighConfidence
    {
        get => _s.PreselectOnlyHighConfidence;
        set { _s.PreselectOnlyHighConfidence = value; Save(); }
    }

    public bool ShowSystemComponents
    {
        get => _s.ShowSystemComponents;
        set { _s.ShowSystemComponents = value; Save(); }
    }

    public int BackupRetentionDays
    {
        get => _s.BackupRetentionDays;
        set { _s.BackupRetentionDays = Math.Clamp(value, 0, 3650); Save(); }
    }

    public int[] RetentionOptions { get; } = [7, 14, 30, 90, 365, 0];

    public bool IsAdmin => MainViewModel.Instance?.IsAdmin ?? false;
    public string DataFolder => AppPaths.Root;

    public ObservableCollection<ToggleItem> SwitchedOff { get; } = [];

    protected override Task LoadAsync()
    {
        SwitchedOff.Clear();
        foreach (var t in ToggleService.DisabledItems()) SwitchedOff.Add(t);
        return Task.CompletedTask;
    }

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _s.Save();
        OnPropertyChanged(name);
    }

    [RelayCommand]
    private void OpenDataFolder() => Dialogs.OpenInExplorer(AppPaths.Root);

    [RelayCommand]
    private void OpenLogs() => Dialogs.OpenInExplorer(AppPaths.Logs);

    [RelayCommand]
    private void EditBlocklist()
    {
        var file = AppPaths.UserBlocklistFile;
        if (!File.Exists(file))
        {
            File.WriteAllText(file, """
                {
                  "entries": [
                    { "pattern": "\\bExample Toolbar\\b", "type": "Toolbar", "description": "Replace this with programs or extensions you want flagged." }
                  ]
                }
                """);
        }
        Dialogs.OpenUrl(file);
    }

    [RelayCommand]
    private async Task PruneBackups()
    {
        var n = await Task.Run(() => BackupMaintenance.PruneOld(BackupRetentionDays));
        Dialogs.Info(n == 0 ? "No backups are old enough to delete." : $"Deleted {n} old backup(s).");
    }

    [RelayCommand]
    private async Task Reenable(ToggleItem? item)
    {
        if (item is null) return;
        if (await Task.Run(() => ToggleService.Enable(item))) SwitchedOff.Remove(item);
        else Dialogs.Info($"Could not switch {item.Display} back on.");
    }
}
