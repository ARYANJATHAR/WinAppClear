using WinAppClear.Core.Models;
using WinAppClear.Core.Util;
using Microsoft.Win32;

namespace WinAppClear.Core.Inventory;

/// <summary>
/// Estimates when programs were last used from Explorer's UserAssist data and Windows Prefetch files.
/// </summary>
public static class UsageTracker
{
    private const string UserAssistKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    // Known folder GUIDs used in UserAssist paths instead of drive paths.
    private static readonly Dictionary<string, Func<string>> KnownFolderGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = () => KnownPaths.ProgramFiles,
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = () => KnownPaths.ProgramFilesX86,
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = () => KnownPaths.System32,
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = () => KnownPaths.Windows,
        ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = () => Path.Combine(KnownPaths.Windows, "SysWOW64"),
    };

    /// <summary>Map of executable path → last run time.</summary>
    public static Dictionary<string, DateTime> ReadLastRunTimes()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(UserAssistKey);
            if (root is not null)
            {
                foreach (var guid in root.GetSubKeyNames())
                {
                    using var count = root.OpenSubKey($@"{guid}\Count");
                    if (count is null) continue;
                    foreach (var encoded in count.GetValueNames())
                    {
                        if (count.GetValue(encoded) is not byte[] data || data.Length < 68) continue;
                        var name = Rot13(encoded);
                        var path = ResolveKnownFolder(name);
                        if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        var ft = BitConverter.ToInt64(data, 60);
                        if (ft <= 0) continue;
                        var time = DateTime.FromFileTime(ft);
                        if (!result.TryGetValue(path, out var existing) || existing < time) result[path] = time;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"UserAssist read failed: {ex.Message}");
        }

        // Prefetch files (readable when elevated): NAME.EXE-HASH.pf, last write = last run.
        try
        {
            if (Directory.Exists(KnownPaths.Prefetch))
            {
                foreach (var pf in Directory.EnumerateFiles(KnownPaths.Prefetch, "*.pf"))
                {
                    var file = Path.GetFileNameWithoutExtension(pf);
                    var dash = file.LastIndexOf('-');
                    if (dash <= 0) continue;
                    var exeName = file[..dash];
                    var time = File.GetLastWriteTime(pf);
                    var key = "prefetch:" + exeName;
                    if (!result.TryGetValue(key, out var existing) || existing < time) result[key] = time;
                }
            }
        }
        catch
        {
            // Not elevated — UserAssist alone is fine.
        }

        return result;
    }

    /// <summary>Fills <see cref="InstalledProgram.LastUsed"/> for each program from the given run times.</summary>
    public static void Apply(IEnumerable<InstalledProgram> programs, Dictionary<string, DateTime> runTimes)
    {
        foreach (var p in programs)
        {
            var folder = ProgramInventory.GuessInstallFolder(p);
            DateTime? best = null;
            if (folder is not null)
            {
                foreach (var (path, time) in runTimes)
                {
                    if (path.StartsWith("prefetch:", StringComparison.Ordinal)) continue;
                    if (PathUtil.IsStrictlyUnder(path, folder) && (best is null || time > best)) best = time;
                }
            }

            var icon = PathUtil.ExtractExecutablePath(p.DisplayIcon);
            if (icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (runTimes.TryGetValue(icon, out var t) && (best is null || t > best)) best = t;
                if (runTimes.TryGetValue("prefetch:" + Path.GetFileName(icon).ToUpperInvariant(), out var pt) &&
                    (best is null || pt > best)) best = pt;
            }
            p.LastUsed = best;
        }
    }

    private static string? ResolveKnownFolder(string name)
    {
        if (name.Length > 38 && name[0] == '{' && name[37] == '}')
        {
            var guid = name[..38];
            if (KnownFolderGuids.TryGetValue(guid, out var resolver)) return resolver() + name[38..];
            return null;
        }
        return name.Length > 2 && name[1] == ':' ? name : null;
    }

    public static string Rot13(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is >= 'a' and <= 'z') chars[i] = (char)('a' + (c - 'a' + 13) % 26);
            else if (c is >= 'A' and <= 'Z') chars[i] = (char)('A' + (c - 'A' + 13) % 26);
        }
        return new string(chars);
    }
}
