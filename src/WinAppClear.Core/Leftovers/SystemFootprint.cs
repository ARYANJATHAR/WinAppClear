using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Leftovers;

/// <summary>
/// A one-time capture of system-wide items (services, tasks, startup entries, firewall rules, shortcuts,
/// shell extensions) so many programs can be matched against it without re-reading Windows each time.
/// </summary>
public sealed class SystemFootprint
{
    private readonly Lazy<List<ServiceInfo>> _services = new(() => ServiceManager.GetServices());
    private readonly Lazy<List<ScheduledTaskInfo>> _tasks = new(() => TaskSchedulerService.GetTasks());
    private readonly Lazy<List<StartupEntry>> _startup = new(StartupManager.GetEntries);
    private readonly Lazy<List<FirewallRuleInfo>> _firewall = new(FirewallService.GetRules);
    private readonly Lazy<List<ShortcutInfo>> _shortcuts = new(ShortcutService.GetAllShortcuts);
    private readonly Lazy<List<ShellExtensionInfo>> _shellExtensions = new(ShellExtensionService.GetContextMenuHandlers);

    public IReadOnlyList<ServiceInfo> Services => _services.Value;
    public IReadOnlyList<ScheduledTaskInfo> Tasks => _tasks.Value;
    public IReadOnlyList<StartupEntry> Startup => _startup.Value;
    public IReadOnlyList<FirewallRuleInfo> FirewallRules => _firewall.Value;
    public IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts.Value;
    public IReadOnlyList<ShellExtensionInfo> ShellExtensions => _shellExtensions.Value;

    public IEnumerable<ServiceInfo> ServicesUnder(string folder) =>
        Services.Where(s => s.BinaryPath is not null && PathUtil.IsStrictlyUnder(s.BinaryPath, folder));

    public IEnumerable<ScheduledTaskInfo> TasksUnder(string folder) =>
        Tasks.Where(t => t.ExecutablePath is not null && PathUtil.IsStrictlyUnder(t.ExecutablePath, folder));

    public IEnumerable<StartupEntry> StartupUnder(string folder) =>
        Startup.Where(s => s.TargetPath is not null && PathUtil.IsStrictlyUnder(s.TargetPath, folder));

    public IEnumerable<FirewallRuleInfo> FirewallUnder(string folder) =>
        FirewallRules.Where(r => r.ApplicationPath is not null && PathUtil.IsStrictlyUnder(r.ApplicationPath, folder));

    public IEnumerable<ShortcutInfo> ShortcutsUnder(string folder) =>
        Shortcuts.Where(s => s.Target is not null && PathUtil.IsStrictlyUnder(s.Target, folder));

    public IEnumerable<ShellExtensionInfo> ShellExtensionsUnder(string folder) =>
        ShellExtensions.Where(s => s.DllPath is not null && PathUtil.IsStrictlyUnder(s.DllPath, folder));
}
