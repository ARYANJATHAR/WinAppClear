using WinAppClear.Core.Util;
using Microsoft.Win32;

namespace WinAppClear.Core.Win;

public enum StartupLocation
{
    UserRun,
    MachineRun,
    MachineRun32,
    UserStartupFolder,
    CommonStartupFolder,
}

public sealed record StartupEntry(
    string Name,
    string Command,
    string? TargetPath,
    StartupLocation Location,
    bool Enabled)
{
    /// <summary>Registry key holding the entry (Run keys) — null for startup-folder shortcuts.</summary>
    public string? RegistryKey => Location switch
    {
        StartupLocation.UserRun => @"HKCU\" + StartupManager.RunKey,
        StartupLocation.MachineRun => @"HKLM\" + StartupManager.RunKey,
        StartupLocation.MachineRun32 => @"HKLM\" + StartupManager.RunKey,
        _ => null,
    };

    public string RegistryViewName => Location == StartupLocation.MachineRun32 ? "32" : "64";

    /// <summary>For startup-folder items: the .lnk/.exe file path.</summary>
    public string? FilePath => Location switch
    {
        StartupLocation.UserStartupFolder => Path.Combine(KnownPaths.Startup, Name),
        StartupLocation.CommonStartupFolder => Path.Combine(KnownPaths.CommonStartup, Name),
        _ => null,
    };

    public string LocationLabel => Location switch
    {
        StartupLocation.UserRun => "Run (current user)",
        StartupLocation.MachineRun => "Run (all users)",
        StartupLocation.MachineRun32 => "Run (all users, 32-bit)",
        StartupLocation.UserStartupFolder => "Startup folder (current user)",
        StartupLocation.CommonStartupFolder => "Startup folder (all users)",
        _ => Location.ToString(),
    };

    public string Id => $"{Location}|{Name}";
}

/// <summary>
/// Lists programs that start with Windows and enables/disables them the same way Task Manager does
/// (the StartupApproved registry flags), so changes are reversible and visible in Task Manager.
/// </summary>
public static class StartupManager
{
    public const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<StartupEntry> GetEntries()
    {
        var list = new List<StartupEntry>();
        ReadRun(list, RegistryHive.CurrentUser, RegistryView.Registry64, StartupLocation.UserRun);
        ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry64, StartupLocation.MachineRun);
        ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry32, StartupLocation.MachineRun32);
        ReadFolder(list, KnownPaths.Startup, StartupLocation.UserStartupFolder);
        ReadFolder(list, KnownPaths.CommonStartup, StartupLocation.CommonStartupFolder);
        return list;
    }

    private static void ReadRun(List<StartupEntry> list, RegistryHive hive, RegistryView view, StartupLocation location)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(RunKey);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var command = key.GetValue(name)?.ToString() ?? "";
                list.Add(new StartupEntry(name, command, PathUtil.ExtractExecutablePath(command), location,
                    IsApproved(location, name)));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Read Run key {hive}/{view}: {ex.Message}");
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, StartupLocation location)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutService.GetTarget(file) : file;
                list.Add(new StartupEntry(name, target ?? file, target, location, IsApproved(location, name)));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Read startup folder {folder}: {ex.Message}");
        }
    }

    private static (RegistryHive Hive, string SubKey) ApprovalKey(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => (RegistryHive.CurrentUser, ApprovedKey + @"\Run"),
        StartupLocation.MachineRun => (RegistryHive.LocalMachine, ApprovedKey + @"\Run"),
        StartupLocation.MachineRun32 => (RegistryHive.LocalMachine, ApprovedKey + @"\Run32"),
        StartupLocation.UserStartupFolder => (RegistryHive.CurrentUser, ApprovedKey + @"\StartupFolder"),
        StartupLocation.CommonStartupFolder => (RegistryHive.LocalMachine, ApprovedKey + @"\StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(location)),
    };

    private static bool IsApproved(StartupLocation location, string name)
    {
        try
        {
            var (hive, sub) = ApprovalKey(location);
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(sub);
            if (key?.GetValue(name) is byte[] data && data.Length > 0)
                return (data[0] & 0x1) == 0; // 02/06 = enabled, 03/07 = disabled
        }
        catch
        {
            // Treat unreadable flag as enabled.
        }
        return true;
    }

    public static bool SetEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            var (hive, sub) = ApprovalKey(entry.Location);
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).CreateSubKey(sub, writable: true);
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
            key.SetValue(entry.Name, data, RegistryValueKind.Binary);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Set startup {entry.Name} enabled={enabled}: {ex.Message}");
            return false;
        }
    }
}
