using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using WinAppClear.App.Infrastructure;
using WinAppClear.Core.Android;
using WinAppClear.Core.Browsers;
using WinAppClear.Core.Cleanup;
using WinAppClear.Core.History;
using WinAppClear.Core.Models;
using WinAppClear.Core.Store;
using WinAppClear.Core.Util;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinAppClear.App.ViewModels;

// ---------------- Windows (Store) apps ----------------

public partial class StoreAppRow : ObservableObject
{
    public StoreAppRow(StoreApp app) => App = app;

    public StoreApp App { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ImageSource? _icon;

    public string Name => App.DisplayName;
    public string Publisher => App.Publisher;
    public string Version => App.Version;
    public DateTime? Installed => App.InstalledDate;
    public bool IsRemovable => App.IsRemovable;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
}

public partial class StoreAppsViewModel : PageViewModel
{
    private List<StoreAppRow> _all = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showProtected;

    public ObservableCollection<StoreAppRow> Rows { get; } = [];
    public ICollectionView View { get; }
    public override string Title => "Windows Apps";
    public override string Subtitle => "Apps installed from the Microsoft Store or preinstalled with Windows. Parts of Windows itself are protected.";

    public StoreAppsViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is StoreAppRow r && (ShowProtected || r.IsRemovable) &&
                           (string.IsNullOrWhiteSpace(Search) || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                            r.Publisher.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnShowProtectedChanged(bool value) => View.Refresh();

    protected override Task LoadAsync() => Reload();

    [RelayCommand]
    private Task Refresh() => RunBusy("Reading Windows apps…", Reload);

    private async Task Reload()
    {
        var apps = await Task.Run(StoreAppService.GetApps);
        _all = apps.Select(a => new StoreAppRow(a)).ToList();
        Rows.Clear();
        foreach (var r in _all) Rows.Add(r);
        View.Refresh();
        StatusText = $"{_all.Count(a => a.IsRemovable)} removable apps";
        foreach (var r in _all)
        {
            var logo = IconCache.ResolveStoreLogo(r.App.LogoPath);
            r.Icon = await IconCache.GetAsync(logo);
        }
    }

    [RelayCommand]
    private async Task Uninstall(StoreAppRow? row)
    {
        List<StoreAppRow> targets = row is not null ? [row] : _all.Where(r => r.IsSelected).ToList();
        targets = targets.Where(t => t.IsRemovable).ToList();
        if (targets.Count == 0)
        {
            Dialogs.Info("Tick the apps you want to remove.");
            return;
        }
        var names = string.Join("\n• ", targets.Select(t => t.Name));
        if (!Dialogs.Confirm($"Remove these apps for your account?\n\n• {names}\n\nYou can reinstall them from the Microsoft Store.")) return;

        await RunBusy("Removing apps…", async () =>
        {
            var failures = new List<string>();
            foreach (var t in targets)
            {
                BusyText = $"Removing {t.Name}…";
                var (ok, message) = await StoreAppService.RemoveAsync(t.App);
                if (!ok)
                {
                    failures.Add($"{t.Name}: {message}");
                    continue;
                }

                // Remove the app's leftover data folder if Windows left it behind.
                var leftovers = new List<LeftoverItem>();
                if (Directory.Exists(t.App.DataFolder))
                    leftovers.Add(new LeftoverItem
                    {
                        Kind = LeftoverKind.Folder, Path = t.App.DataFolder, Confidence = Confidence.High,
                        Reason = "App data folder", SizeBytes = PathUtil.DirectorySize(t.App.DataFolder),
                    });
                var report = leftovers.Count > 0 ? await Task.Run(() => CleanupExecutor.Execute($"App data of {t.Name}", leftovers)) : null;
                HistoryStore.Add(new HistoryEntry
                {
                    Name = t.Name, Publisher = t.Publisher, Version = t.Version, Method = RemovalMethod.StoreApp,
                    Result = "Removed", LeftoversFound = leftovers.Count, LeftoversRemoved = report?.Removed ?? 0,
                    BackupId = report?.BackupId, BytesFreed = leftovers.Sum(l => l.SizeBytes ?? 0),
                });
            }
            await Reload();
            if (failures.Count > 0) Dialogs.Info("Some apps could not be removed:\n\n" + string.Join("\n", failures));
        });
    }
}

// ---------------- Browser extensions ----------------

public partial class ExtensionRow : ObservableObject
{
    public ExtensionRow(BrowserExtension ext) => Ext = ext;

    public BrowserExtension Ext { get; }

    [ObservableProperty] private bool _isSelected;

    public string Name => Ext.Name;
    public string Browser => Ext.BrowserName;
    public string Profile => Ext.Profile;
    public string Version => Ext.Version;
    public string? Description => Ext.Description;
    public bool Enabled => Ext.Enabled;
    public bool BroadAccess => Ext.HasBroadAccess;
    public string Access => Ext.HasBroadAccess ? "Can read and change data on websites" : Ext.Permissions.Count == 0 ? "No special permissions" : $"{Ext.Permissions.Count} permission(s)";
    public string BrowserGlyph => Ext.Browser switch
    {
        BrowserKind.Firefox => "",
        _ => "",
    };
}

public partial class ExtensionsViewModel : PageViewModel
{
    private List<ExtensionRow> _all = [];

    [ObservableProperty] private string _search = "";
    public ObservableCollection<ExtensionRow> Rows { get; } = [];
    public ICollectionView View { get; }
    public override string Title => "Browser Extensions";
    public override string Subtitle => "Extensions in Chrome, Edge, Brave, Vivaldi and Firefox. Extensions that can read every website deserve a second look.";

    public ExtensionsViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ExtensionRow r && (string.IsNullOrWhiteSpace(Search) ||
                                                    r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                                                    r.Browser.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSearchChanged(string value) => View.Refresh();

    protected override Task LoadAsync() => Reload();

    [RelayCommand]
    private Task Refresh() => RunBusy("Reading browser extensions…", Reload);

    private async Task Reload()
    {
        var list = await Task.Run(BrowserExtensionService.GetExtensions);
        _all = list.Select(e => new ExtensionRow(e)).ToList();
        Rows.Clear();
        foreach (var r in _all) Rows.Add(r);
        View.Refresh();
        StatusText = $"{_all.Count} extensions in {_all.Select(r => r.Browser).Distinct().Count()} browser(s)";
    }

    [RelayCommand]
    private void OpenInBrowser(ExtensionRow? row)
    {
        if (row is not null) BrowserExtensionService.OpenManagePage(row.Ext);
    }

    [RelayCommand]
    private void OpenStorePage(ExtensionRow? row)
    {
        if (row is not null) Dialogs.OpenUrl(row.Ext.StoreUrl);
    }

    [RelayCommand]
    private async Task Remove(ExtensionRow? row)
    {
        List<ExtensionRow> targets = row is not null ? [row] : _all.Where(r => r.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Dialogs.Info("Tick the extensions you want to remove.");
            return;
        }
        var running = targets.Select(t => t.Ext.Browser).Distinct().Where(BrowserExtensionService.IsBrowserRunning).ToList();
        if (running.Count > 0)
        {
            Dialogs.Info($"Please close {string.Join(" and ", running)} first. Browsers rewrite their extension list while open, " +
                         "so removing extensions only works while the browser is closed.\n\n" +
                         "Tip: \"Open in browser\" lets you remove an extension from the browser's own page instead.");
            return;
        }
        if (!Dialogs.Confirm($"Remove {targets.Count} extension(s)? Their files are backed up first.")) return;

        await RunBusy("Removing extensions…", async () =>
        {
            var failures = new List<string>();
            foreach (var t in targets)
            {
                var (ok, msg) = await Task.Run(() => BrowserExtensionService.Remove(t.Ext));
                if (!ok) failures.Add($"{t.Name}: {msg}");
                else
                    HistoryStore.Add(new HistoryEntry
                    {
                        Name = $"{t.Name} ({t.Browser} extension)", Version = t.Version, Method = RemovalMethod.BrowserExtension,
                        Result = msg,
                    });
            }
            await Reload();
            Dialogs.Info(failures.Count == 0 ? "Done. Open the browser to check — it finishes cleaning up when it starts."
                : "Some extensions could not be removed:\n\n" + string.Join("\n", failures));
        });
    }
}

// ---------------- Android apps ----------------

public partial class AndroidAppRow : ObservableObject
{
    public AndroidAppRow(AndroidApp app) => App = app;

    public AndroidApp App { get; }

    [ObservableProperty] private bool _isSelected;

    public string Package => App.Package;
    public string Kind => App.IsSystem ? "System" : "Installed";
}

public partial class AndroidViewModel : PageViewModel
{
    [ObservableProperty] private AndroidDevice? _selectedDevice;
    [ObservableProperty] private bool _includeSystem;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string? _setupHint;

    public ObservableCollection<AndroidDevice> Devices { get; } = [];
    public ObservableCollection<AndroidAppRow> Rows { get; } = [];
    public ICollectionView View { get; }
    public override string Title => "Android Apps";
    public override string Subtitle => "Remove apps from an Android phone connected by USB (turn on USB debugging in Developer options).";

    public AndroidViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is AndroidAppRow r && (string.IsNullOrWhiteSpace(Search) || r.Package.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnSelectedDeviceChanged(AndroidDevice? value) => _ = RunBusy("Reading apps…", LoadApps);
    partial void OnIncludeSystemChanged(bool value) => _ = RunBusy("Reading apps…", LoadApps);

    protected override Task LoadAsync() => FindDevices();

    [RelayCommand]
    private Task Refresh() => RunBusy("Looking for devices…", FindDevices);

    private async Task FindDevices()
    {
        var (devices, error) = await AdbService.GetDevicesAsync();
        Devices.Clear();
        foreach (var d in devices) Devices.Add(d);
        SetupHint = error ?? (devices.Count == 0
            ? "No phone found. Connect it with a USB cable, enable USB debugging, and accept the prompt on the phone."
            : devices.Any(d => d.State == "unauthorized") ? "Unlock your phone and tap \"Allow\" on the USB debugging prompt, then refresh." : null);
        SelectedDevice = devices.FirstOrDefault(d => d.State == "device");
        if (SelectedDevice is null) Rows.Clear();
    }

    private async Task LoadApps()
    {
        Rows.Clear();
        if (SelectedDevice is not { State: "device" } device) return;
        var apps = await AdbService.GetAppsAsync(device.Serial, IncludeSystem);
        foreach (var a in apps) Rows.Add(new AndroidAppRow(a));
        StatusText = $"{apps.Count} apps on {device.Model ?? device.Serial}";
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (SelectedDevice is null) return;
        var targets = Rows.Where(r => r.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Dialogs.Info("Tick the apps you want to remove.");
            return;
        }
        var system = targets.Count(t => t.App.IsSystem);
        var warn = system > 0
            ? $"\n\n{system} of them are system apps. They will be removed for your user only and can be restored with " +
              "\"adb shell cmd package install-existing <package>\". Removing the wrong system app can break the phone."
            : "";
        if (!Dialogs.Confirm($"Uninstall {targets.Count} app(s) from {SelectedDevice.Model ?? SelectedDevice.Serial}?{warn}", warning: system > 0))
            return;

        await RunBusy("Uninstalling…", async () =>
        {
            var failures = new List<string>();
            foreach (var t in targets)
            {
                BusyText = $"Uninstalling {t.Package}…";
                var (ok, msg) = await AdbService.UninstallAsync(SelectedDevice.Serial, t.App);
                if (!ok) failures.Add($"{t.Package}: {msg}");
                else HistoryStore.Add(new HistoryEntry { Name = t.Package, Method = RemovalMethod.AndroidApp, Result = "Uninstalled from phone" });
            }
            await LoadApps();
            if (failures.Count > 0) Dialogs.Info(string.Join("\n", failures));
        });
    }
}
