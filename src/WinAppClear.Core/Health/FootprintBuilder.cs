using WinAppClear.Core.Inventory;
using WinAppClear.Core.Leftovers;
using WinAppClear.Core.Models;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Health;

/// <summary>Builds the list of background "permissions" (toggle items) a program currently has switched on.</summary>
public static class FootprintBuilder
{
    public static List<ToggleItem> ActiveItems(InstalledProgram program, SystemFootprint footprint,
        bool includeShellAndFirewall = true)
    {
        var folder = ProgramInventory.GuessInstallFolder(program);
        if (folder is null || PathUtil.IsUnderOrEqual(folder, KnownPaths.Windows)) return [];
        var owner = program.DisplayName;
        var items = new List<ToggleItem>();

        foreach (var e in footprint.StartupUnder(folder).Where(e => e.Enabled))
            items.Add(new ToggleItem
            {
                Kind = ToggleKind.Startup, Owner = owner, Key = e.Id, Display = $"{e.Name} ({e.LocationLabel})",
            });

        foreach (var s in footprint.ServicesUnder(folder).Where(s => s.IsAutoStart && !s.IsDriver))
        {
            if (Safety.SafetyGuard.IsProtectedServiceName(s.Name)) continue;
            items.Add(new ToggleItem
            {
                Kind = ToggleKind.Service, Owner = owner, Key = s.Name, Display = $"{s.DisplayName} (starts automatically)",
            });
        }

        foreach (var t in footprint.TasksUnder(folder).Where(t => t.Enabled))
            items.Add(new ToggleItem
            {
                Kind = ToggleKind.ScheduledTask, Owner = owner, Key = t.Path, Display = $"{t.Name} — {t.Command}",
            });

        if (includeShellAndFirewall)
        {
            foreach (var x in footprint.ShellExtensionsUnder(folder).Where(x => !x.Blocked).DistinctBy(x => x.Clsid))
                items.Add(new ToggleItem
                {
                    Kind = ToggleKind.ShellExtension, Owner = owner, Key = x.Clsid,
                    Display = $"{x.HandlerName} (right-click menu: {x.Scope})",
                });

            foreach (var r in footprint.FirewallUnder(folder).Where(r => r.Enabled && r.Inbound && r.Allow).DistinctBy(r => r.Name))
                items.Add(new ToggleItem
                {
                    Kind = ToggleKind.FirewallRule, Owner = owner, Key = r.Name, Extra = r.ApplicationPath,
                    Display = $"{r.Name} (allows incoming connections)",
                });
        }
        return items;
    }

    /// <summary>Items that keep a program running in the background (used for hibernation).</summary>
    public static List<ToggleItem> BackgroundItems(InstalledProgram program, SystemFootprint footprint) =>
        ActiveItems(program, footprint, includeShellAndFirewall: false);
}
