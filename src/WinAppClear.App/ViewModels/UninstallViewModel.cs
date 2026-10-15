using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using WinAppClear.App.Infrastructure;
using WinAppClear.Core.Cleanup;
using WinAppClear.Core.History;
using WinAppClear.Core.Models;
using WinAppClear.Core.Uninstall;
using WinAppClear.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinAppClear.App.ViewModels;

public enum UninstallStage
{
    Confirm,
    Running,
    Review,
    Removing,
    Done,
}

public partial class UninstallJob : ObservableObject
{
    public UninstallJob(InstalledProgram program) => Program = program;

    public InstalledProgram Program { get; }
    public string Name => Program.DisplayName;

    [ObservableProperty] private string _status = "Waiting";
    [ObservableProperty] private string _tone = "Muted";

    public UninstallResult? Result { get; set; }
    public List<LeftoverItem> Leftovers { get; } = [];
}

public partial class LeftoverRow : ObservableObject
{
    public LeftoverRow(LeftoverItem item, string owner, bool selected, bool showOwner = false)
    {
        Item = item;
        Owner = owner;
        _isSelected = selected;
        OwnerPrefix = showOwner ? $"{owner} · " : "";
    }

    public LeftoverItem Item { get; }
    public string Owner { get; }
    public string OwnerPrefix { get; }

    [ObservableProperty] private bool _isSelected;

    public string Kind => Item.KindLabel;
    public string Path => Item.DisplayPath;
    public string Reason => Item.Reason;
    public string? Detail => Item.Detail;
    public Confidence Confidence => Item.Confidence;
    public long? Size => Item.SizeBytes;
    public string KindGlyph => Item.Kind switch
    {
        LeftoverKind.Folder => "",
        LeftoverKind.File => "",
        LeftoverKind.Shortcut => "",
        LeftoverKind.RegistryKey or LeftoverKind.RegistryValue => "",
        LeftoverKind.Service => "",
        LeftoverKind.ScheduledTask => "",
        LeftoverKind.StartupEntry => "",
        LeftoverKind.FirewallRule => "",
        _ => "",
    };
}

/// <summary>The uninstall wizard: options → run uninstallers → review leftovers → remove → summary.</summary>
public partial class UninstallViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private CleanupReport? _report;

    public UninstallViewModel(IReadOnlyList<InstalledProgram> programs, bool forceMode = false)
    {
        foreach (var p in programs) Jobs.Add(new UninstallJob(p));
        _createRestorePoint = AppSettings.Current.CreateRestorePoint;
        _quiet = AppSettings.Current.QuietUninstallByDefault;
        QuietAvailable = programs.Any(p => UninstallCommand.Quiet(p) is not null);
        ForceMode = forceMode;
        LeftoversView = CollectionViewSource.GetDefaultView(Leftovers);
        ShowOwner = Jobs.Count > 1;
    }

    public ObservableCollection<UninstallJob> Jobs { get; } = [];
    public ObservableCollection<LeftoverRow> Leftovers { get; } = [];
    public ICollectionView LeftoversView { get; }
    public bool QuietAvailable { get; }

    /// <summary>When several programs are uninstalled together, each leftover shows which program it belongs to.</summary>
    public bool ShowOwner { get; }

    /// <summary>Skip the program's own uninstaller and go straight to finding what to remove.</summary>
    public bool ForceMode { get; }

    /// <summary>Replaces the standard leftover scan (Force Uninstall of a folder, Install Monitor logs).</summary>
    public Func<InstalledProgram, CancellationToken, Task<List<LeftoverItem>>>? CustomScan { get; init; }

    public string Explanation => ForceMode
        ? "WinAppClear will find everything that belongs to this program and let you choose what to remove. Nothing is deleted without a backup."
        : "WinAppClear runs each program's own uninstaller, then finds what it left behind. Nothing is deleted without a backup.";

    public string Heading => Jobs.Count == 1 ? $"Uninstall {Jobs[0].Name}" : $"Uninstall {Jobs.Count} programs";

    [ObservableProperty] private UninstallStage _stage = UninstallStage.Confirm;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _quiet;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private string? _restorePointNote;

    public event Action? RequestClose;

    public bool IsConfirm => Stage == UninstallStage.Confirm;
    public bool IsRunning => Stage is UninstallStage.Running or UninstallStage.Removing;
    public bool IsReview => Stage == UninstallStage.Review;
    public bool IsDone => Stage == UninstallStage.Done;

    partial void OnStageChanged(UninstallStage value)
    {
        OnPropertyChanged(nameof(IsConfirm));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsDone));
    }

    [RelayCommand]
    private async Task Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Stage = UninstallStage.Running;
        var progress = new Progress<string>(s => ProgressText = s);

        try
        {
            if (CreateRestorePoint)
            {
                ProgressText = "Creating a System Restore point (this can take a minute)…";
                var rp = await UninstallWorkflow.CreateRestorePointAsync(Jobs.Select(j => j.Program));
                RestorePointNote = rp.Success ? "Restore point created." : $"No restore point: {rp.Message}";
            }

            foreach (var job in Jobs)
            {
                ct.ThrowIfCancellationRequested();
                if (!ForceMode)
                {
                    job.Status = "Running the program's uninstaller…";
                    job.Tone = "Info";
                    ProgressText = $"Uninstalling {job.Name}. Follow the uninstaller's window if one appears.";
                    job.Result = await UninstallRunner.RunAsync(job.Program, Quiet, progress, ct);
                    (job.Status, job.Tone) = job.Result.Outcome switch
                    {
                        UninstallOutcome.Removed => (job.Result.Message, "Accent"),
                        UninstallOutcome.NoUninstaller => ("Uninstaller missing — scanning what's left", "Warning"),
                        UninstallOutcome.Cancelled => ("Cancelled — the program is still installed", "Warning"),
                        _ => (job.Result.Message, "Warning"),
                    };
                    var scan = job.Result.Outcome is UninstallOutcome.Removed or UninstallOutcome.NoUninstaller;
                    if (!scan)
                    {
                        UninstallWorkflow.Record(job.Program, Quiet ? RemovalMethod.Quiet : RemovalMethod.Standard, job.Result, [], null);
                        continue;
                    }
                }

                job.Status = "Scanning for leftovers…";
                ProgressText = $"Looking for files and registry entries {job.Name} left behind…";
                var items = CustomScan is not null
                    ? await CustomScan(job.Program, ct)
                    : await UninstallWorkflow.ScanLeftoversAsync(job.Program, ct);
                job.Leftovers.AddRange(items);
                var preselectMedium = !AppSettings.Current.PreselectOnlyHighConfidence;
                foreach (var i in items)
                {
                    var row = new LeftoverRow(i, job.Name, i.Confidence == Confidence.High || (preselectMedium && i.Confidence == Confidence.Medium), ShowOwner);
                    row.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(LeftoverRow.IsSelected)) UpdateSelectionSummary();
                    };
                    Leftovers.Add(row);
                }
                job.Status = items.Count == 0 ? $"{job.Status.TrimEnd('.')}. No leftovers found." : $"{items.Count} leftover(s) found";
                if (job.Tone == "Info") job.Tone = "Accent";
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var j in Jobs.Where(j => j.Status == "Waiting")) j.Status = "Skipped";
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall wizard", ex);
            Dialogs.Error($"Something went wrong: {ex.Message}");
        }

        UpdateSelectionSummary();
        if (Leftovers.Count > 0)
        {
            Stage = UninstallStage.Review;
        }
        else
        {
            RecordAll();
            Finish();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cts is { IsCancellationRequested: false }) _cts.Cancel();
        ProgressText = "Stopping after the current step…";
    }

    [RelayCommand]
    private void SelectHigh()
    {
        foreach (var r in Leftovers) r.IsSelected = r.Confidence == Confidence.High;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in Leftovers) r.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Leftovers) r.IsSelected = false;
    }

    [RelayCommand]
    private void OpenItem(LeftoverRow? row)
    {
        if (row is null) return;
        if (row.Item.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue ||
            (row.Item.Kind == LeftoverKind.StartupEntry && row.Item.ValueName is not null))
            Dialogs.OpenRegedit(row.Item.Path);
        else if (row.Item.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut)
            Dialogs.OpenInExplorer(row.Item.Path);
    }

    private void UpdateSelectionSummary()
    {
        var selected = Leftovers.Where(l => l.IsSelected).ToList();
        SelectionSummary = $"{selected.Count} of {Leftovers.Count} selected · {PathUtil.FormatSize(selected.Sum(s => s.Size ?? 0))}";
    }

    [RelayCommand]
    private async Task RemoveSelected()
    {
        var selected = Leftovers.Where(l => l.IsSelected).Select(l => l.Item).ToList();
        if (selected.Count == 0)
        {
            SkipRemoval();
            return;
        }
        if (selected.Any(s => s.Confidence != Confidence.High) &&
            !Dialogs.Confirm("Some selected items are only Medium/Low confidence matches. They will be backed up first, " +
                             "but please make sure they really belong to the removed program.\n\nContinue?", warning: true))
            return;

        Stage = UninstallStage.Removing;
        _cts = new CancellationTokenSource();
        var title = Jobs.Count == 1 ? $"Leftovers of {Jobs[0].Name}" : $"Leftovers of {Jobs.Count} programs";
        _report = await UninstallWorkflow.RemoveAsync(title, selected, new Progress<string>(s => ProgressText = s), _cts.Token);
        RecordAll();
        Finish();
    }

    [RelayCommand]
    private void SkipRemoval()
    {
        RecordAll();
        Finish();
    }

    private void RecordAll()
    {
        foreach (var job in Jobs.Where(j => ForceMode || j.Result is { Outcome: UninstallOutcome.Removed or UninstallOutcome.NoUninstaller }))
        {
            var method = ForceMode ? RemovalMethod.Forced : Quiet ? RemovalMethod.Quiet : RemovalMethod.Standard;
            UninstallWorkflow.Record(job.Program, method, job.Result, job.Leftovers, _report);
        }
    }

    private void Finish()
    {
        var removedPrograms = Jobs.Count(j => ForceMode || j.Result?.Outcome == UninstallOutcome.Removed);
        var lines = new List<string>();
        if (!ForceMode) lines.Add($"{removedPrograms} of {Jobs.Count} program(s) uninstalled.");
        if (_report is not null)
        {
            var freed = _report.Results.Where(r => r.Status is CleanupStatus.Removed or CleanupStatus.ScheduledForReboot)
                .Sum(r => r.Item.SizeBytes ?? 0);
            lines.Add($"{_report.Removed} leftover(s) removed, freeing {PathUtil.FormatSize(freed)}.");
            if (_report.Scheduled > 0) lines.Add($"{_report.Scheduled} item(s) were in use and will be removed when you restart Windows.");
            if (_report.Failed > 0) lines.Add($"{_report.Failed} item(s) could not be removed (see History).");
            if (_report.Skipped > 0) lines.Add($"{_report.Skipped} protected item(s) were skipped for safety.");
            lines.Add("Everything removed was backed up — restore it any time from History & Backups.");
        }
        else if (Leftovers.Count == 0 && Jobs.Any(j => j.Result?.Outcome == UninstallOutcome.Removed))
        {
            lines.Add("No leftovers were found — the uninstaller cleaned up after itself.");
        }
        if (Jobs.Any(j => j.Result is { ExitCode: 3010 or 1641 })) lines.Add("Restart Windows to finish uninstalling.");
        if (RestorePointNote is not null) lines.Add(RestorePointNote);
        Summary = string.Join("\n", lines);
        Stage = UninstallStage.Done;
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
