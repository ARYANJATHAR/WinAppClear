using System.Globalization;
using System.Text.RegularExpressions;
using WinAppClear.Core.Models;
using WinAppClear.Core.Util;
using Microsoft.Win32;

namespace WinAppClear.Core.Inventory;

/// <summary>Reads installed desktop programs from the Uninstall keys (64-bit, 32-bit and per-user).</summary>
public static partial class ProgramInventory
{
    public const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View)[] Sources =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
    ];

    [GeneratedRegex(@"\{[0-9A-Fa-f\-]{36}\}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^KB\d{6,}$", RegexOptions.IgnoreCase)]
    private static partial Regex HotfixPattern();

    /// <summary>Lists installed programs, hiding system components, updates and duplicate entries.</summary>
    public static List<InstalledProgram> GetInstalledPrograms(bool includeSystemComponents = false)
    {
        var result = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in Sources)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallSubKey);
            if (uninstall is null) continue;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null) continue;
                    var program = Read(key, hive, view, name, includeSystemComponents);
                    if (program is null) continue;

                    // The same product can appear in both views (shared keys on some systems).
                    var dedupe = $"{program.DisplayName}|{program.DisplayVersion}|{program.UninstallString}";
                    if (!seen.Add(dedupe)) continue;
                    result.Add(program);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Skipping uninstall key {name}: {ex.Message}");
                }
            }
        }

        return result.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static InstalledProgram? Read(RegistryKey key, RegistryHive hive, RegistryView view, string keyName,
        bool includeSystemComponents = false)
    {
        var displayName = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var uninstallString = (key.GetValue("UninstallString") as string)?.Trim();
        var systemComponent = AsInt(key.GetValue("SystemComponent")) == 1;
        var parentKey = key.GetValue("ParentKeyName") as string;
        var releaseType = key.GetValue("ReleaseType") as string;

        if (!includeSystemComponents)
        {
            if (systemComponent) return null;
            if (!string.IsNullOrEmpty(parentKey)) return null; // updates/patches of another product
            if (releaseType is "Update" or "Hotfix" or "Security Update" or "Service Pack") return null;
            if (HotfixPattern().IsMatch(keyName)) return null;
            if (string.IsNullOrWhiteSpace(uninstallString)) return null;
        }

        var isMsi = AsInt(key.GetValue("WindowsInstaller")) == 1;
        string? productCode = null;
        if (Guid.TryParse(keyName, out _)) productCode = keyName;
        else if (isMsi && uninstallString is not null)
        {
            var m = GuidPattern().Match(uninstallString);
            if (m.Success) productCode = m.Value;
        }

        var stamp = RegistryPath.LastWriteTime(key);
        var sizeKb = AsLong(key.GetValue("EstimatedSize"));

        return new InstalledProgram
        {
            Id = $"{RegistryPath.HiveShortName(hive)}|{RegistryPath.ViewName(view)}|{keyName}",
            DisplayName = displayName,
            Publisher = (key.GetValue("Publisher") as string)?.Trim(),
            DisplayVersion = (key.GetValue("DisplayVersion") as string)?.Trim(),
            InstallLocation = PathUtil.Normalize(key.GetValue("InstallLocation") as string),
            UninstallString = uninstallString,
            QuietUninstallString = (key.GetValue("QuietUninstallString") as string)?.Trim(),
            ModifyPath = (key.GetValue("ModifyPath") as string)?.Trim(),
            DisplayIcon = (key.GetValue("DisplayIcon") as string)?.Trim(),
            UrlInfoAbout = (key.GetValue("URLInfoAbout") as string)?.Trim(),
            IsWindowsInstaller = isMsi,
            NoRepair = AsInt(key.GetValue("NoRepair")) == 1,
            NoModify = AsInt(key.GetValue("NoModify")) == 1,
            MsiProductCode = productCode,
            InstallDate = ParseInstallDate(key.GetValue("InstallDate") as string) ?? stamp,
            RegistryTimestamp = stamp,
            EstimatedSizeBytes = sizeKb > 0 ? sizeKb * 1024 : null,
            RegistryKeyPath = $"{RegistryPath.HiveShortName(hive)}\\{UninstallSubKey}\\{keyName}",
            Hive = hive,
            View = view,
            KeyName = keyName,
        };
    }

    /// <summary>
    /// A stand-in for something that isn't (or is no longer) a registered program — e.g. a folder chosen for
    /// Force Uninstall. Its registry key path deliberately points nowhere.
    /// </summary>
    public static InstalledProgram Placeholder(string name, string? folder, string? publisher = null)
    {
        var id = Guid.NewGuid().ToString("N");
        return new InstalledProgram
        {
            Id = $"placeholder|{id}",
            DisplayName = name,
            Publisher = publisher,
            InstallLocation = PathUtil.Normalize(folder),
            RegistryKeyPath = $@"HKLM\SOFTWARE\WinAppClear\Placeholder\{id}",
            Hive = RegistryHive.LocalMachine,
            View = RegistryView.Registry64,
            KeyName = id,
        };
    }

    public static bool StillInstalled(InstalledProgram program)
    {
        using var key = RegistryPath.Open(program.RegistryKeyPath, program.View);
        return key?.GetValue("DisplayName") is string;
    }

    /// <summary>The folder a program most likely lives in: InstallLocation, else the uninstaller's or icon's folder.</summary>
    public static string? GuessInstallFolder(InstalledProgram p)
    {
        if (p.InstallLocation is not null && !Safety.SafetyGuard.IsProtectedPath(p.InstallLocation) &&
            !p.InstallLocation.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase))
            return p.InstallLocation;

        foreach (var candidate in new[] { p.DisplayIcon, p.UninstallString })
        {
            var exe = PathUtil.ExtractExecutablePath(candidate);
            if (exe is null) continue;
            if (exe.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = Path.GetDirectoryName(exe);
            if (dir is null) continue;
            if (PathUtil.IsUnderOrEqual(dir, KnownPaths.Windows)) continue;
            if (PathUtil.IsUnderOrEqual(dir, KnownPaths.Temp)) continue;
            // Installer caches hold a copy of the setup program, not the program itself.
            if (PathUtil.IsUnderOrEqual(dir, Path.Combine(KnownPaths.ProgramData, "Package Cache"))) continue;
            if (PathUtil.IsUnderOrEqual(dir, Path.Combine(KnownPaths.LocalAppData, "Package Cache"))) continue;
            if (PathUtil.IsUnderOrEqual(dir, Path.Combine(KnownPaths.Windows, "Installer"))) continue;
            // Uninstallers often live in a sub-folder like "uninst" — step up one level.
            var leaf = Path.GetFileName(dir).ToLowerInvariant();
            if (leaf is "uninst" or "uninstall" or "uninstaller" or "_uninst" or "bin" && Path.GetDirectoryName(dir) is { } parent)
                dir = parent;
            if (!Safety.SafetyGuard.IsProtectedPath(dir)) return dir;
        }
        return null;
    }

    private static DateTime? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out d)) return d;
        return null;
    }

    private static int AsInt(object? v) => v switch
    {
        int i => i,
        string s when int.TryParse(s, out var i) => i,
        _ => 0,
    };

    private static long AsLong(object? v) => v switch
    {
        int i => (uint)i,
        long l => l,
        string s when long.TryParse(s, out var l) => l,
        _ => 0,
    };
}
