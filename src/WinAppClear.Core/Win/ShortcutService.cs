using System.Runtime.InteropServices;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Win;

public sealed record ShortcutInfo(string Path, string? Target);

/// <summary>Resolves .lnk shortcut targets via WScript.Shell.</summary>
public static class ShortcutService
{
    private static readonly object Gate = new();
    private static dynamic? _shell;

    public static string? GetTarget(string lnkPath)
    {
        lock (Gate)
        {
            try
            {
                _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                dynamic link = _shell!.CreateShortcut(lnkPath);
                string target = link.TargetPath;
                Marshal.FinalReleaseComObject(link);
                return string.IsNullOrWhiteSpace(target) ? null : PathUtil.Normalize(target);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>All shortcuts in the Start Menu and Desktop folders (current user and all users).</summary>
    public static List<ShortcutInfo> GetAllShortcuts()
    {
        var roots = new[]
        {
            KnownPaths.StartMenuPrograms, KnownPaths.CommonStartMenuPrograms, KnownPaths.Desktop, KnownPaths.CommonDesktop,
        };
        var list = new List<ShortcutInfo>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            var recurse = !root.Equals(KnownPaths.Desktop, StringComparison.OrdinalIgnoreCase) &&
                          !root.Equals(KnownPaths.CommonDesktop, StringComparison.OrdinalIgnoreCase);
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = recurse,
                    IgnoreInaccessible = true,
                });
                foreach (var f in files) list.Add(new ShortcutInfo(f, GetTarget(f)));
            }
            catch
            {
                // Skip unreadable folder.
            }
        }
        return list;
    }
}
