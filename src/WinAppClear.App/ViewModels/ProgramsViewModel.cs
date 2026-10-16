using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using WinAppClear.App.Infrastructure;
using WinAppClear.App.Views;
using WinAppClear.Core.Health;
using WinAppClear.Core.Inventory;
using WinAppClear.Core.Leftovers;
using WinAppClear.Core.Models;
using WinAppClear.Core.Uninstall;
using WinAppClear.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinAppClear.App.ViewModels;

public enum ProgramFilter
{
    All,
    Recent,
    Large,
    Infrequent,
    Together,
}

public partial class ProgramRow : ObservableObject
{
    public ProgramRow(InstalledProgram program)
    {
        Program = program;
    }

    public InstalledProgram Program { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isHibernated;
    [ObservableProperty] private string? _groupLabel;

    public string Name => Program.DisplayName;
    public string Publisher => Program.Publisher ?? "";
    public string Version => Program.DisplayVersion ?? "";
    public long? Size => Program.SizeBytes;
    public DateTime? InstallDate => Program.InstallDate;
    public DateTime? LastUsed => Program.LastUsed;
    public string Architecture => Program.ArchitectureLabel;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public bool HasQuietUninstall => UninstallCommand.Quiet(Program) is not null;
    public string? InstallFolder => ProgramInventory.GuessInstallFolder(Program);

    public void RefreshSize() => OnPropertyChanged(nameof(Size));
}

public partial class ProgramsViewModel : PageViewModel
{
    private List<ProgramRow> _all = [];

    [ObservableProperty] private ProgramFilter _filter = ProgramFilter.All;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ProgramRow? _selectedRow;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _summary = "";

    public ObservableCollection<ProgramRow> Rows { get; } = [];
    public ICollectionView View { get; }

    public override string Title => Filter switch
    {
        ProgramFilter.Recent => "Recently Installed",
        ProgramFilter.Large => "Large Programs",
        ProgramFilter.Infrequent => "Infrequently Used",
        ProgramFilter.Together => "Installed Together",
        _ => "All Programs",
    };

    public override string? Subtitle => Filter switch
    {
        ProgramFilter.Recent => "Programs installed in the last 30 days.",
        ProgramFilter.Large => "Programs using the most disk space.",
        ProgramFilter.Infrequent => "Programs you haven't opened for 60 days or more (when Windows recorded it).",
        ProgramFilter.Together => "Programs installed within minutes of each other — often extra software (bundleware) slipped in by another installer.",
        _ => "Everything installed on this PC. Tick programs to uninstall several at once.",
    };

    public ProgramsViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ProgramRow r && Matches(r);
        View.CollectionChanged += (_, _) => UpdateVisibleSummary();
    }

    partial void OnFilterChanged(ProgramFilter value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        ApplyFilter();
    }

    partial void OnSearchChanged(string value)
    {
        View.Refresh();
        UpdateVisibleSummary();
    }

    private bool Matches(ProgramRow r)
    {
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var query = Search.Trim();
        return r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               r.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task LoadAsync() => await ReloadAsync();

    [RelayCommand]
    private Task Refresh() => RunBusy("Reading installed programs…", ReloadAsync);

    private async Task ReloadAsync()
    {
        var programs = await Task.Run(() =>
        {
            var list = ProgramInventory.GetInstalledPrograms(AppSettings.Current.ShowSystemComponents);
            UsageTracker.Apply(list, UsageTracker.ReadLastRunTimes());
            return list;
        });
        var hibernated = HibernationService.Hibernated().Select(h => h.ProgramId).ToHashSet();
        _all = programs.Select(p => new ProgramRow(p) { IsHibernated = hibernated.Contains(p.Id) }).ToList();
        foreach (var row in _all) row.PropertyChanged += RowChanged;
        ApplyFilter();
        MainViewModel.Instance?.SetBadge("All Programs", _all.Count.ToString());
        _ = LoadIconsAsync(_all);
        _ = MeasureMissingSizesAsync(_all);
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProgramRow.IsSelected)) SelectedCount = _all.Count(r => r.IsSelected);
        if (e.PropertyName == nameof(ProgramRow.Size)) UpdateVisibleSummary();
    }

    private void ApplyFilter()
    {
        IEnumerable<ProgramRow> rows = _all;
        foreach (var r in _all) r.GroupLabel = null;
        switch (Filter)
        {
            case ProgramFilter.Recent:
                rows = _all.Where(r => r.InstallDate is { } d && DateTime.Now - d < TimeSpan.FromDays(30))
                    .OrderByDescending(r => r.InstallDate);
                break;
            case ProgramFilter.Large:
                rows = _all.Where(r => r.Size is > 0).OrderByDescending(r => r.Size).Take(40);
                break;
            case ProgramFilter.Infrequent:
                rows = _all.Where(r => r.LastUsed is { } u && DateTime.Now - u > TimeSpan.FromDays(60))
                    .OrderBy(r => r.LastUsed);
                break;
            case ProgramFilter.Together:
            {
                var groups = ProgramLookup.InstalledTogether(_all.Select(r => r.Program));
                var byId = _all.ToDictionary(r => r.Program.Id);
                var list = new List<ProgramRow>();
                var n = 0;
                foreach (var g in groups)
                {
                    n++;
                    foreach (var p in g)
                    {
                        var row = byId[p.Id];
                        row.GroupLabel = $"Group {n} · {g[0].RegistryTimestamp:g}";
                        list.Add(row);
                    }
                }
                rows = list;
                break;
            }
            default:
                rows = _all;
                break;
        }

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        View.Refresh();
        UpdateVisibleSummary();
    }

    private void UpdateVisibleSummary()
    {
        var visible = View.Cast<ProgramRow>().ToList();
        var searching = !string.IsNullOrWhiteSpace(Search);
        var label = visible.Count == 1 ? "program" : "programs";
        Summary = $"{visible.Count} {(searching ? "matching " : "")}{label} · {PathUtil.FormatSize(visible.Sum(r => r.Size ?? 0))}";
        EmptyText = visible.Count > 0 ? null : searching
            ? $"No programs match \"{Search.Trim()}\". Try another name or publisher."
            : Filter switch
        {
            ProgramFilter.Recent => "Nothing was installed in the last 30 days.",
            ProgramFilter.Infrequent => "No programs are known to be unused for 60+ days. (Windows only records use for some programs.)",
            ProgramFilter.Together => "No programs were installed together with software from another publisher.",
            _ => "No programs found.",
        };
    }

    [ObservableProperty] private string? _emptyText;

    private static async Task LoadIconsAsync(List<ProgramRow> rows)
    {
        foreach (var row in rows)
        {
            var exe = row.InstallFolder is { } f && Directory.Exists(f)
                ? SafeFirstExe(f)
                : null;
            row.Icon = await IconCache.GetAsync(row.Program.DisplayIcon, exe);
        }
    }

    private static string? SafeFirstExe(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*.exe").FirstOrDefault(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)); }
        catch { return null; }
    }

    private async Task MeasureMissingSizesAsync(List<ProgramRow> rows)
    {
        foreach (var row in rows.Where(r => r.Program.EstimatedSizeBytes is null))
        {
            var folder = row.InstallFolder;
            if (folder is null || !Directory.Exists(folder)) continue;
            row.Program.MeasuredSizeBytes = await Task.Run(() => PathUtil.DirectorySize(folder));
            row.RefreshSize();
        }
    }

    private List<InstalledProgram> TargetPrograms()
    {
        var ticked = _all.Where(r => r.IsSelected).Select(r => r.Program).ToList();
        if (ticked.Count > 0) return ticked;
        return SelectedRow is null ? [] : [SelectedRow.Program];
    }

    [RelayCommand]
    private async Task Uninstall(ProgramRow? row)
    {
        List<InstalledProgram> targets = row is not null ? [row.Program] : TargetPrograms();
        if (targets.Count == 0)
        {
            Dialogs.Info("Select a program first (click a row, or tick several).");
            return;
        }
        var window = new UninstallWindow(new UninstallViewModel(targets));
        window.ShowDialog();
        await RunBusy("Refreshing…", ReloadAsync);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in _all) r.IsSelected = false;
    }

    [RelayCommand]
    private void OpenLocation(ProgramRow? row) => Dialogs.OpenInExplorer((row ?? SelectedRow)?.InstallFolder);

    [RelayCommand]
    private void OpenRegistry(ProgramRow? row)
    {
        var r = row ?? SelectedRow;
        if (r is not null) Dialogs.OpenRegedit(r.Program.RegistryKeyPath);
    }

    [RelayCommand]
    private void SearchOnline(ProgramRow? row)
    {
        var r = row ?? SelectedRow;
        if (r is not null) Dialogs.OpenUrl($"https://www.bing.com/search?q={Uri.EscapeDataString(r.Name + " " + r.Publisher)}");
    }

    [RelayCommand]
    private void Repair(ProgramRow? row)
    {
        var r = row ?? SelectedRow;
        if (r is null) return;
        if (!UninstallRunner.StartRepair(r.Program)) Dialogs.Info($"{r.Name} doesn't offer a repair or modify option.");
    }

    [RelayCommand]
    private void CopyName(ProgramRow? row)
    {
        var r = row ?? SelectedRow;
        if (r is not null) Dialogs.CopyText(r.Name);
    }

    [RelayCommand]
    private async Task Hibernate(ProgramRow? row)
    {
        var r = row ?? SelectedRow;
        if (r is null) return;
        if (r.IsHibernated)
        {
            var record = HibernationService.Hibernated().FirstOrDefault(h => h.ProgramId == r.Program.Id);
            if (record is null) return;
            var (_, msg) = await Task.Run(() => HibernationService.Wake(record));
            r.IsHibernated = false;
            StatusText = msg;
            return;
        }
        if (!Dialogs.Confirm($"Put {r.Name} to sleep?\n\nIts startup entries, background services and scheduled tasks will be switched off " +
                             "so it stops running in the background. You can wake it any time from here or from Software Health."))
            return;
        await RunBusy($"Hibernating {r.Name}…", async () =>
        {
            var (ok, msg) = await Task.Run(() => HibernationService.Hibernate(r.Program, new SystemFootprint(), closeRunning: true));
            r.IsHibernated = ok || r.IsHibernated;
            StatusText = msg;
            if (!ok) Dialogs.Info(msg);
        });
    }
}
