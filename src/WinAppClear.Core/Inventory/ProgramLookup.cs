using WinAppClear.Core.Models;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Inventory;

/// <summary>Helpers that relate programs to files and to each other.</summary>
public static class ProgramLookup
{
    /// <summary>Finds the installed program an executable belongs to (used by Easy Uninstall).</summary>
    public static InstalledProgram? FindByExecutable(IEnumerable<InstalledProgram> programs, string exePath)
    {
        InstalledProgram? best = null;
        var bestLength = -1;
        foreach (var p in programs)
        {
            var icon = PathUtil.ExtractExecutablePath(p.DisplayIcon);
            if (icon is not null && icon.Equals(exePath, StringComparison.OrdinalIgnoreCase)) return p;

            var folder = ProgramInventory.GuessInstallFolder(p);
            if (folder is null || !PathUtil.IsStrictlyUnder(exePath, folder)) continue;
            if (folder.Length > bestLength)
            {
                best = p;
                bestLength = folder.Length; // the deepest matching folder is the most specific program
            }
        }
        return best;
    }

    /// <summary>
    /// Groups programs whose uninstall entries were written within a few minutes of each other — usually a
    /// main program plus "bundleware" that its installer added.
    /// </summary>
    public static List<List<InstalledProgram>> InstalledTogether(IEnumerable<InstalledProgram> programs, TimeSpan? window = null)
    {
        var w = window ?? TimeSpan.FromMinutes(3);
        var sorted = programs.Where(p => p.RegistryTimestamp is not null).OrderBy(p => p.RegistryTimestamp).ToList();
        var groups = new List<List<InstalledProgram>>();
        List<InstalledProgram>? current = null;
        foreach (var p in sorted)
        {
            if (current is not null && p.RegistryTimestamp!.Value - current[^1].RegistryTimestamp!.Value <= w)
            {
                current.Add(p);
            }
            else
            {
                if (current is { Count: > 1 }) groups.Add(current);
                current = [p];
            }
        }
        if (current is { Count: > 1 }) groups.Add(current);

        // Windows updates touch many Microsoft entries at once — those groups aren't bundleware.
        return groups.Where(g =>
                g.Select(p => NameMatcher.NormalizePublisher(p.Publisher)).Distinct().Count() > 1 &&
                !g.All(p => (p.Publisher ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(g => g[0].RegistryTimestamp)
            .ToList();
    }
}
