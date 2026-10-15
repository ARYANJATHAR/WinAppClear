using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Windows;
using WinAppClear.App.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinAppClear.App.ViewModels;

public partial class NavItem : ObservableObject
{
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required string Section { get; init; }
    public required string HelpText { get; init; }
    public required Func<PageViewModel> Page { get; init; }
    public Action<PageViewModel>? OnSelect { get; init; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _badge;

    public Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnBadgeChanged(string? value) => OnPropertyChanged(nameof(BadgeVisibility));
}

public sealed class NavSection
{
    public required string Title { get; init; }
    public required List<NavItem> Items { get; init; }
}

public partial class MainViewModel : ObservableObject
{
    private readonly ProgramsViewModel _programs = new();
    private readonly Dictionary<Type, PageViewModel> _pages = new();

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private string _currentHelpText = "";

    public ObservableCollection<NavSection> Sections { get; } = [];
    public bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public string Version => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";

    public static MainViewModel? Instance { get; private set; }

    public MainViewModel()
    {
        Instance = this;
        T Page<T>() where T : PageViewModel, new()
        {
            if (!_pages.TryGetValue(typeof(T), out var p)) _pages[typeof(T)] = p = new T();
            return (T)p;
        }

        NavItem ProgramsNav(string title, string glyph, ProgramFilter filter, string help) => new()
        {
            Title = title, Glyph = glyph, Section = "Programs", Page = () => _programs,
            HelpText = help,
            OnSelect = _ => _programs.Filter = filter,
        };

        Sections.Add(new NavSection
        {
            Title = "PROGRAMS",
            Items =
            [
                ProgramsNav("All Programs", "", ProgramFilter.All, "Browse installed desktop programs. Select one or tick several to uninstall, then review the leftovers before removing them."),
                ProgramsNav("Recently Installed", "", ProgramFilter.Recent, "Review programs installed in the last 30 days. Useful when a new installation may have caused a problem."),
                ProgramsNav("Large Programs", "", ProgramFilter.Large, "Find programs using the most disk space and choose which ones you no longer need. Sizes may be estimated."),
                ProgramsNav("Infrequently Used", "", ProgramFilter.Infrequent, "Find programs Windows records as unused for at least 60 days. Windows does not record usage for every program."),
                ProgramsNav("Installed Together", "", ProgramFilter.Together, "Review programs installed within minutes of each other to spot possible bundled extras. Being grouped together does not mean a program is unwanted."),
            ],
        });
        Sections.Add(new NavSection
        {
            Title = "APPS",
            Items =
            [
                new NavItem { Title = "Windows Apps", Glyph = "", Section = "Apps", Page = Page<StoreAppsViewModel>, HelpText = "Review Microsoft Store apps and remove supported apps you no longer need. Protected system apps cannot be removed here." },
                new NavItem { Title = "Browser Extensions", Glyph = "", Section = "Apps", Page = Page<ExtensionsViewModel>, HelpText = "Find unwanted browser add-ons and toolbars. Close the browser before removing an extension so it can release its files." },
                new NavItem { Title = "Android Apps", Glyph = "", Section = "Apps", Page = Page<AndroidViewModel>, HelpText = "Manage apps on a connected Android phone. Requires Android Platform Tools (ADB), USB debugging and authorization on the phone. System apps are removed for the current user only." },
            ],
        });
        Sections.Add(new NavSection
        {
            Title = "CARE",
            Items =
            [
                new NavItem { Title = "Software Health", Glyph = "", Section = "Care", Page = Page<HealthViewModel>, HelpText = "Scan for leftovers, broken uninstallers, old installers, caches and unwanted background activity. Review each finding before applying a fix. The unwanted-software list is a basic check, not antivirus." },
                new NavItem { Title = "Software Updates", Glyph = "", Section = "Care", Page = Page<UpdatesViewModel>, HelpText = "Find and install updates through Windows Package Manager (winget). Requires winget and an internet connection; not every installed program is supported." },
                new NavItem { Title = "Install Monitor", Glyph = "", Section = "Care", Page = Page<MonitorViewModel>, HelpText = "Start monitoring before installing a program, then finish afterward to record changes for later cleanup. Other activity during this time can appear in the results, so review them before removal." },
            ],
        });
        Sections.Add(new NavSection
        {
            Title = "TOOLS",
            Items =
            [
                new NavItem { Title = "Force Uninstall", Glyph = "", Section = "Tools", Page = Page<ForceUninstallViewModel>, HelpText = "Clean up a broken installation or leftover program folder when normal uninstall does not work. This skips the original uninstaller. Choose only the program's own folder and review all matches." },
                new NavItem { Title = "Easy Uninstall", Glyph = "", Section = "Tools", Page = Page<EasyUninstallViewModel>, HelpText = "Identify a program by dragging the target onto its open window or choosing it from the window list, then start its uninstall." },
                new NavItem { Title = "Force Delete", Glyph = "", Section = "Tools", Page = Page<ForceDeleteViewModel>, HelpText = "Remove stubborn files or folders that are in use. WinAppClear can close locking programs or schedule removal at restart. Save your work first; permanent deletion has no backup." },
                new NavItem { Title = "File Shredder", Glyph = "", Section = "Tools", Page = Page<ShredderViewModel>, HelpText = "Overwrite files before permanently deleting them. There is no backup or undo. Complete erasure is not guaranteed on SSDs because of wear leveling." },
            ],
        });
        Sections.Add(new NavSection
        {
            Title = "",
            Items =
            [
                new NavItem { Title = "History & Backups", Glyph = "", Section = "More", Page = Page<HistoryViewModel>, HelpText = "Review past removals and restore items WinAppClear backed up. Restoring leftovers does not reinstall a complete program. Deleting a backup removes that recovery option." },
                new NavItem { Title = "Settings", Glyph = "", Section = "More", Page = Page<SettingsViewModel>, HelpText = "Set restore-point, quiet-uninstall and leftover-selection preferences, choose backup retention, open logs, or re-enable supported background items you switched off. Changes save immediately." },
            ],
        });

        Navigate(Sections[0].Items[0]);
    }

    public IEnumerable<NavItem> AllItems => Sections.SelectMany(s => s.Items);

    [RelayCommand]
    private void Navigate(NavItem item)
    {
        foreach (var i in AllItems) i.IsSelected = i == item;
        var page = item.Page();
        item.OnSelect?.Invoke(page);
        CurrentPage = page;
        CurrentHelpText = item.HelpText;
        _ = page.OnShownAsync();
    }

    /// <summary>Lets other pages jump to a section (e.g. "Uninstall" from Easy Uninstall).</summary>
    public void NavigateTo(string title)
    {
        var item = AllItems.FirstOrDefault(i => i.Title == title);
        if (item is not null) Navigate(item);
    }

    public ProgramsViewModel Programs => _programs;

    public void SetBadge(string title, string? badge)
    {
        var item = AllItems.FirstOrDefault(i => i.Title == title);
        if (item is not null) item.Badge = badge;
    }
}
