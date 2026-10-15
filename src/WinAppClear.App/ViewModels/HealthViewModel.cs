using System.Collections.ObjectModel;
using WinAppClear.App.Infrastructure;
using WinAppClear.Core.Health;
using WinAppClear.Core.Updates;
using WinAppClear.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinAppClear.App.ViewModels;

public partial class HealthIssueRow : ObservableObject
{
    public HealthIssueRow(HealthIssue issue)
    {
        Issue = issue;
        _isSelected = issue.Recommended;
    }

    public HealthIssue Issue { get; }

    [ObservableProperty] private bool _isSelected;

    public string Title => Issue.Title;
    public string? Detail => Issue.Detail;
    public long? Size => Issue.SizeBytes;
    public HealthSeverity Severity => Issue.Severity;
    public bool CanFix => Issue.Payload is not null;
}

public partial class HealthCategoryViewModel : ObservableObject
{
    private readonly HealthViewModel _owner;

    public HealthCategoryViewModel(IHealthCheck check, HealthViewModel owner)
    {
        Check = check;
        _owner = owner;
    }

    public IHealthCheck Check { get; }
    public string Title => Check.Title;
    public string Description => Check.Description;
    public string FixLabel => Check.FixLabel;

    public string Glyph => Check.Category switch
    {
        HealthCategory.Leftovers => "",
        HealthCategory.Outdated => "",
        HealthCategory.Permissions => "",
        HealthCategory.Notifications => "",
        HealthCategory.UninstallIssues => "",
        HealthCategory.InstallationFiles => "",
        HealthCategory.Malicious => "",
        HealthCategory.RedundantFiles => "",
        HealthCategory.Hibernation => "",
        _ => "",
    };

    public ObservableCollection<HealthIssueRow> Issues { get; } = [];

    [ObservableProperty] private string _state = "Not scanned";
    [ObservableProperty] private string _tone = "Muted";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string? _error;

    public int Count => Issues.Count(i => i.CanFix);
    public long TotalSize => Issues.Sum(i => i.Size ?? 0);
    public bool HasIssues => Issues.Count > 0;

    public void SetResults(HealthCheckResult result)
    {
        Issues.Clear();
        foreach (var i in result.Issues) Issues.Add(new HealthIssueRow(i));
        Error = result.Error;
        var count = Count;
        (State, Tone) = result.Error is not null ? ("Couldn't check", "Warning")
            : count == 0 ? (Issues.Count > 0 ? "See details" : "All good", Issues.Count > 0 ? "Info" : "Accent")
            : Check.Category is HealthCategory.Malicious ? ($"{count} found", "Danger")
            : ($"{count} found", "Warning");
        if (TotalSize > 0 && count > 0) State += $" · {PathUtil.FormatSize(TotalSize)}";
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(HasIssues));
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Issues) i.IsSelected = i.CanFix;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Issues) i.IsSelected = false;
    }

    [RelayCommand]
    private Task Fix() => _owner.FixAsync(this);
}

public partial class HealthViewModel : PageViewModel
{
    public HealthViewModel()
    {
        foreach (var check in HealthScanner.AllChecks) Categories.Add(new HealthCategoryViewModel(check, this));
        UpdateHeader();
    }

    public override string Title => "Software Health";
    public override string Subtitle => "One scan, nine checks: leftovers, updates, permissions, notifications, broken uninstallers, installer files, unwanted software, junk files and sleeping programs.";

    public ObservableCollection<HealthCategoryViewModel> Categories { get; } = [];
    public ObservableCollection<HibernationRecord> Hibernated { get; } = [];
    public ObservableCollection<ToggleItem> SwitchedOff { get; } = [];

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _headlineDetail = "";
    [ObservableProperty] private string _headlineTone = "Muted";
    [ObservableProperty] private bool _scanning;

    protected override Task LoadAsync()
    {
        ReloadLists();
        return Task.CompletedTask;
    }

    private void ReloadLists()
    {
        Hibernated.Clear();
        foreach (var h in HibernationService.Hibernated()) Hibernated.Add(h);
        SwitchedOff.Clear();
        foreach (var t in ToggleService.DisabledItems().OrderByDescending(t => t.DisabledAt)) SwitchedOff.Add(t);
    }

    private void UpdateHeader()
    {
        var last = AppSettings.Current.LastHealthScan;
        var scanned = Categories.Any(c => c.State != "Not scanned");
        if (!scanned)
        {
            Headline = "Software Status: Needs a scan";
            HeadlineDetail = last is null
                ? "Your software has never been checked. Scan now — nothing is changed until you choose to fix something."
                : $"Last checked {(DateTime.Now - last.Value).Days} day(s) ago. Scan again to see the current state.";
            HeadlineTone = "Warning";
            return;
        }
        var total = Categories.Sum(c => c.Count);
        var danger = Categories.Where(c => c.Check.Category == HealthCategory.Malicious).Sum(c => c.Count);
        Headline = total == 0 ? "Software Status: Good" : $"Software Status: {total} item(s) to review";
        HeadlineDetail = total == 0 ? "Everything looks fine." :
            danger > 0 ? $"{danger} unwanted program(s) or extension(s) found — review them first." :
            "Expand a section to review and fix what you choose.";
        HeadlineTone = danger > 0 ? "Danger" : total == 0 ? "Accent" : "Warning";
    }

    [RelayCommand]
    private async Task Scan()
    {
        Scanning = true;
        foreach (var c in Categories)
        {
            c.State = "Waiting…";
            c.Tone = "Muted";
        }
        var context = new HealthContext();
        var progress = new Progress<(IHealthCheck Check, HealthCheckResult? Result)>(update =>
        {
            var cat = Categories.First(c => c.Check == update.Check);
            if (update.Result is null)
            {
                cat.State = "Checking…";
                cat.IsWorking = true;
            }
            else
            {
                cat.IsWorking = false;
                cat.SetResults(update.Result);
            }
        });
        try
        {
            await HealthScanner.ScanAllAsync(context, progress);
            AppSettings.Current.LastHealthScan = DateTime.Now;
            AppSettings.Current.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Health scan", ex);
            Dialogs.Error($"The scan stopped: {ex.Message}");
        }
        finally
        {
            Scanning = false;
            foreach (var c in Categories) c.IsWorking = false;
            UpdateHeader();
            var total = Categories.Sum(c => c.Count);
            MainViewModel.Instance?.SetBadge("Software Health", total > 0 ? total.ToString() : null);
            var updates = Categories.First(c => c.Check.Category == HealthCategory.Outdated).Count;
            MainViewModel.Instance?.SetBadge("Software Updates", updates > 0 ? updates.ToString() : null);
        }
    }

    public async Task FixAsync(HealthCategoryViewModel category)
    {
        var selected = category.Issues.Where(i => i.IsSelected && i.CanFix).Select(i => i.Issue).ToList();
        if (selected.Count == 0)
        {
            Dialogs.Info("Tick the items you want to fix first.");
            return;
        }

        var confirm = category.Check.Category switch
        {
            HealthCategory.Malicious => $"Uninstall/remove {selected.Count} item(s)? Program uninstallers may open their own windows. A restore point is created first.",
            HealthCategory.Outdated => $"Update {selected.Count} program(s) with winget? Programs may close while they update.",
            HealthCategory.Permissions => $"Switch off {selected.Count} item(s)? You can switch them back on below, under \"Things you've switched off\".",
            HealthCategory.Notifications => $"Block {selected.Count} notification source(s)? You can undo this below.",
            HealthCategory.Hibernation => $"Put {selected.Count} program(s) to sleep? Wake them any time from the list below.",
            HealthCategory.RedundantFiles => $"Delete these caches and temp files? Programs recreate what they need. Close the listed programs first for the best result.",
            HealthCategory.InstallationFiles => $"Move {selected.Count} installer file(s) to the Recycle Bin?",
            _ => $"Fix {selected.Count} item(s)? Everything removed is backed up first.",
        };
        if (!Dialogs.Confirm(confirm)) return;

        category.IsWorking = true;
        await RunBusy($"{category.FixLabel}…", async () =>
        {
            var result = await category.Check.FixAsync(selected, BusyProgress, CancellationToken.None);
            var context = new HealthContext();
            var rescanned = await category.Check.ScanAsync(context, CancellationToken.None);
            category.SetResults(new HealthCheckResult(category.Check, rescanned, null));
            UpdateHeader();
            ReloadLists();
            var msg = $"{result.Fixed} item(s) done.";
            if (result.Failed > 0) msg += $" {result.Failed} could not be fixed.";
            if (result.Messages.Count > 0) msg += "\n\n" + string.Join("\n", result.Messages.Take(12));
            Dialogs.Info(msg, category.Title);
        });
        category.IsWorking = false;
    }

    [RelayCommand]
    private async Task Wake(HibernationRecord? record)
    {
        if (record is null) return;
        var (_, msg) = await Task.Run(() => HibernationService.Wake(record));
        ReloadLists();
        StatusText = msg;
    }

    [RelayCommand]
    private async Task Reenable(ToggleItem? item)
    {
        if (item is null) return;
        var ok = await Task.Run(() => ToggleService.Enable(item));
        ReloadLists();
        StatusText = ok ? $"Switched back on: {item.Display}" : $"Could not switch on: {item.Display}";
    }
}

public partial class UpdateRow : ObservableObject
{
    public UpdateRow(AvailableUpdate update) => Update = update;

    public AvailableUpdate Update { get; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _status = "";

    public string Name => Update.Name;
    public string Id => Update.Id;
    public string Current => Update.InstalledVersion;
    public string Available => Update.AvailableVersion;
}

public partial class UpdatesViewModel : PageViewModel
{
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _updating;

    public ObservableCollection<UpdateRow> Rows { get; } = [];
    public override string Title => "Software Updates";
    public override string Subtitle => "Keep programs up to date to close security holes. Uses the Windows Package Manager (winget) with silent installs.";

    protected override Task LoadAsync() => Check();

    [RelayCommand]
    private Task Refresh() => RunBusy("Checking for updates…", Check);

    private async Task Check()
    {
        BusyText = "Checking for updates (this can take a minute)…";
        var (updates, error) = await WingetClient.GetUpdatesAsync();
        Rows.Clear();
        foreach (var u in updates) Rows.Add(new UpdateRow(u));
        Message = error ?? (updates.Count == 0 ? "Everything is up to date." : null);
        StatusText = $"{updates.Count} update(s) available";
        MainViewModel.Instance?.SetBadge("Software Updates", updates.Count > 0 ? updates.Count.ToString() : null);
    }

    [RelayCommand]
    private async Task UpdateSelected()
    {
        var targets = Rows.Where(r => r.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Dialogs.Info("Tick the programs to update.");
            return;
        }
        if (!Dialogs.Confirm($"Update {targets.Count} program(s)? Running programs may close while they update.")) return;

        Updating = true;
        var failed = 0;
        foreach (var row in targets)
        {
            row.Status = "Updating…";
            var (ok, output) = await WingetClient.UpgradeAsync(row.Id);
            row.Status = ok ? "Updated" : $"Failed: {output}";
            if (!ok) failed++;
        }
        Updating = false;
        StatusText = failed == 0 ? $"{targets.Count} program(s) updated." : $"{targets.Count - failed} updated, {failed} failed.";
    }
}
