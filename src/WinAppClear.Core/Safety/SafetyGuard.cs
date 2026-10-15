using WinAppClear.Core.Models;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Safety;

/// <summary>
/// The last line of defence: every delete goes through here, and anything that could damage Windows,
/// user data or other programs is refused no matter what a scanner or install log proposed.
/// </summary>
public static class SafetyGuard
{
    private static readonly string[] SystemTopLevelFolders =
    [
        "Users", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "PerfLogs", "Recovery",
        "System Volume Information", "$Recycle.Bin", "$WinREAgent", "Boot", "EFI", "Documents and Settings",
        "OneDriveTemp", "Config.Msi", "Intel", "AMD", "NVIDIA", "inetpub", "Drivers",
    ];

    private static readonly string[] CriticalProgramFilesFolders =
    [
        "Common Files", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail",
        "Windows Media Player", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices",
        "Windows Security", "Windows Sidebar", "WindowsApps", "WindowsPowerShell", "ModifiableWindowsApps",
        "Internet Explorer", "Reference Assemblies", "MSBuild", "dotnet", "Microsoft Update Health Tools",
        "Uninstall Information", "Microsoft.NET", "Windows Kits", "Microsoft SDKs", "PowerShell",
        "Microsoft Office", "Microsoft OneDrive", "Microsoft\\Edge", "Microsoft\\EdgeUpdate", "Microsoft\\EdgeWebView",
    ];

    private static readonly string[] ProtectedDotFolders =
    [
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".config", ".cache", ".local", ".docker", ".m2", ".nuget",
        ".npm", ".cargo", ".rustup", ".gradle", ".conda", ".ipython", ".jupyter", ".pki", ".dotnet",
    ];

    private static readonly string[] CriticalServiceNames =
    [
        "wuauserv", "WinDefend", "mpssvc", "BFE", "EventLog", "Dhcp", "Dnscache", "LanmanServer",
        "LanmanWorkstation", "RpcSs", "RpcEptMapper", "Schedule", "SamSs", "Winmgmt", "TrustedInstaller",
        "msiserver", "CryptSvc", "BITS", "Spooler", "AudioSrv", "Audiosrv", "Themes", "W32Time", "PlugPlay",
        "Power", "ProfSvc", "UserManager", "WSearch", "SecurityHealthService", "wscsvc", "nsi", "Netman",
        "netprofm", "NlaSvc", "DcomLaunch", "LSM", "gpsvc", "EFS", "VSS", "swprv", "seclogon", "Appinfo",
    ];

    private static IEnumerable<string> ExactProtectedFolders()
    {
        yield return KnownPaths.ProgramFiles;
        yield return KnownPaths.ProgramFilesX86;
        yield return KnownPaths.CommonProgramFiles;
        yield return KnownPaths.CommonProgramFilesX86;
        yield return KnownPaths.ProgramData;
        yield return KnownPaths.AppData;
        yield return KnownPaths.LocalAppData;
        yield return KnownPaths.LocalLow;
        yield return KnownPaths.UserProfile;
        yield return KnownPaths.StartMenuPrograms;
        yield return KnownPaths.CommonStartMenuPrograms;
        yield return Path.GetDirectoryName(KnownPaths.StartMenuPrograms)!;
        yield return Path.GetDirectoryName(KnownPaths.CommonStartMenuPrograms)!;
        yield return KnownPaths.Desktop;
        yield return KnownPaths.CommonDesktop;
        yield return KnownPaths.Startup;
        yield return KnownPaths.CommonStartup;
        yield return KnownPaths.Documents;
        yield return KnownPaths.Downloads;
        yield return PathUtil.TrimEnd(KnownPaths.Temp);
        yield return Path.Combine(KnownPaths.LocalAppData, "Temp");
        yield return Path.Combine(KnownPaths.LocalAppData, "Packages");
        yield return Path.Combine(KnownPaths.LocalAppData, "Programs");
        yield return Path.Combine(KnownPaths.ProgramData, "Package Cache");
        yield return AppPaths.Root;
    }

    private static IEnumerable<string> ProtectedTrees()
    {
        yield return KnownPaths.Windows;
        yield return Path.Combine(KnownPaths.AppData, "Microsoft");
        yield return Path.Combine(KnownPaths.LocalAppData, "Microsoft");
        yield return Path.Combine(KnownPaths.ProgramData, "Microsoft");
        yield return AppPaths.Root;
        yield return AppContext.BaseDirectory;
    }

    // Folders inside protected trees whose *children* may still be removed (e.g. Start Menu shortcuts).
    private static IEnumerable<string> AllowedSubtrees()
    {
        yield return KnownPaths.StartMenuPrograms;
        yield return KnownPaths.CommonStartMenuPrograms;
        yield return KnownPaths.Startup;
        yield return KnownPaths.CommonStartup;
    }

    /// <summary>True when the file or folder must never be deleted.</summary>
    public static bool IsProtectedPath(string? rawPath) => ProtectedPathReason(rawPath) is not null;

    public static string? ProtectedPathReason(string? rawPath)
    {
        var path = PathUtil.Normalize(rawPath);
        if (path is null) return "not a full path";
        var root = Path.GetPathRoot(path);
        if (root is null || path.Length <= root.Length) return "drive root";
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return "network path";

        var depth = PathUtil.Depth(path);
        if (depth == 1)
        {
            var name = Path.GetFileName(path);
            if (SystemTopLevelFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) return "system folder";
        }

        if (ExactProtectedFolders().Any(f => string.Equals(PathUtil.TrimEnd(f), path, StringComparison.OrdinalIgnoreCase)))
            return "Windows known folder";

        var inAllowedSubtree = AllowedSubtrees().Any(a => PathUtil.IsStrictlyUnder(path, a));
        if (!inAllowedSubtree && ProtectedTrees().Any(t => PathUtil.IsUnderOrEqual(path, t)))
            return "inside a protected Windows/Microsoft folder";

        foreach (var pf in new[] { KnownPaths.ProgramFiles, KnownPaths.ProgramFilesX86 })
        {
            if (!PathUtil.IsStrictlyUnder(path, pf)) continue;
            var relative = path[(PathUtil.TrimEnd(pf).Length + 1)..];
            foreach (var critical in CriticalProgramFilesFolders)
            {
                if (relative.Equals(critical, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(critical + "\\", StringComparison.OrdinalIgnoreCase))
                    return $"critical folder '{critical}'";
            }
        }

        // Direct children of the user profile (Documents, Pictures, AppData, ...) are user data roots.
        // Program dot-folders (".appname") are allowed, except shared/credential ones.
        if (PathUtil.IsStrictlyUnder(path, KnownPaths.UserProfile) &&
            string.Equals(Path.GetDirectoryName(path), PathUtil.TrimEnd(KnownPaths.UserProfile), StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith('.') || ProtectedDotFolders.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                name.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
                return "user profile folder";
        }

        // Other users' profiles.
        var usersDir = Path.GetDirectoryName(PathUtil.TrimEnd(KnownPaths.UserProfile));
        if (usersDir is not null && PathUtil.IsStrictlyUnder(path, usersDir) &&
            !PathUtil.IsUnderOrEqual(path, KnownPaths.UserProfile) && PathUtil.Depth(path) <= 3)
            return "another user's profile";

        return null;
    }

    private static readonly string[] ProtectedSoftwareRoots =
    [
        "Microsoft", "Policies", "Clients", "RegisteredApplications", "Wow6432Node", "Classes", "ODBC",
        "Windows", "Partner", "DefaultUserEnvironment", "OEM", "Intel", "Realtek", "Khronos",
    ];

    // Paths under SOFTWARE\Microsoft whose child keys may be removed: "<pattern>\<child>".
    private static readonly string[] AllowedMicrosoftKeyParents =
    [
        @"Windows\CurrentVersion\Uninstall",
        @"Windows\CurrentVersion\App Paths",
        @"Tracing",
        @"Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products",
    ];

    // Keys whose *values* may be removed although the key itself is protected.
    private static readonly string[] ValueEditableKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings",
    ];

    /// <summary>True when a registry key must never be deleted.</summary>
    public static bool IsProtectedRegistryKey(string fullPath) => ProtectedRegistryKeyReason(fullPath) is not null;

    public static string? ProtectedRegistryKeyReason(string fullPath)
    {
        string[] segs;
        Microsoft.Win32.RegistryHive hive;
        try
        {
            var parsed = RegistryPath.Parse(fullPath);
            hive = parsed.Hive;
            segs = parsed.SubKey.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return "invalid registry path";
        }

        if (segs.Length < 2) return "top-level registry key";

        switch (hive)
        {
            case Microsoft.Win32.RegistryHive.LocalMachine when segs[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase):
                var isServiceKey = segs.Length == 4 &&
                                   (segs[1].Equals("CurrentControlSet", StringComparison.OrdinalIgnoreCase) ||
                                    segs[1].StartsWith("ControlSet00", StringComparison.OrdinalIgnoreCase)) &&
                                   segs[2].Equals("Services", StringComparison.OrdinalIgnoreCase);
                if (!isServiceKey) return "system configuration key";
                return CriticalServiceNames.Contains(segs[3], StringComparer.OrdinalIgnoreCase) ? "critical Windows service" : null;

            case Microsoft.Win32.RegistryHive.LocalMachine when segs[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase):
            case Microsoft.Win32.RegistryHive.CurrentUser when segs[0].Equals("Software", StringComparison.OrdinalIgnoreCase):
                return SoftwareSubpathReason(segs[1..]);

            case Microsoft.Win32.RegistryHive.ClassesRoot:
                return IsInstallerProductKey(segs) ? null : "class registration key";

            default:
                return "outside the software registry";
        }
    }

    private static string? SoftwareSubpathReason(string[] rest)
    {
        if (rest.Length == 0) return "software root";
        if (rest[0].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[1..];
            if (rest.Length == 0) return "software root";
        }

        if (rest[0].Equals("Classes", StringComparison.OrdinalIgnoreCase))
            return IsInstallerProductKey(rest[1..]) ? null : "class registration key";

        if (rest[0].Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            var sub = string.Join('\\', rest[1..]);
            foreach (var parent in AllowedMicrosoftKeyParents)
            {
                if (sub.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase) &&
                    sub.Length > parent.Length + 1 &&
                    !sub[(parent.Length + 1)..].Contains('\\'))
                    return null;
            }
            return "Microsoft/Windows key";
        }

        if (ProtectedSoftwareRoots.Contains(rest[0], StringComparer.OrdinalIgnoreCase)) return "shared vendor key";
        if (NameMatcher.IsGeneric(NameMatcher.AlphaNumeric(rest[0])) && rest.Length == 1) return "generic key name";
        return null;
    }

    private static bool IsInstallerProductKey(string[] segs) =>
        segs.Length == 3 && segs[0].Equals("Installer", StringComparison.OrdinalIgnoreCase) &&
        (segs[1].Equals("Products", StringComparison.OrdinalIgnoreCase) ||
         segs[1].Equals("Features", StringComparison.OrdinalIgnoreCase) ||
         segs[1].Equals("UpgradeCodes", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a value inside the key must not be deleted.</summary>
    public static bool IsProtectedRegistryValue(string keyPath, string valueName)
    {
        if (string.IsNullOrEmpty(valueName)) return true; // never delete a key's default value blindly
        if (!IsProtectedRegistryKey(keyPath)) return false;
        try
        {
            var (_, sub) = RegistryPath.Parse(keyPath);
            return !ValueEditableKeys.Any(k => sub.Equals(k, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true;
        }
    }

    public static bool IsProtectedServiceName(string serviceName) =>
        CriticalServiceNames.Contains(serviceName, StringComparer.OrdinalIgnoreCase);

    public static bool IsProtectedTaskPath(string taskPath) =>
        taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || taskPath == @"\";

    /// <summary>Checks a proposed leftover; returns null when it may be removed, otherwise the reason.</summary>
    public static string? Check(LeftoverItem item) => item.Kind switch
    {
        LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut => ProtectedPathReason(item.Path),
        LeftoverKind.StartupEntry when item.ValueName is null => ProtectedPathReason(item.Path),
        LeftoverKind.RegistryKey => ProtectedRegistryKeyReason(item.Path),
        LeftoverKind.RegistryValue or LeftoverKind.StartupEntry =>
            IsProtectedRegistryValue(item.Path, item.ValueName!) ? "protected registry value" : null,
        LeftoverKind.Service => IsProtectedServiceName(item.Path) ? "critical Windows service" : null,
        LeftoverKind.ScheduledTask => IsProtectedTaskPath(item.Path) ? "Windows scheduled task" : null,
        LeftoverKind.FirewallRule => null,
        _ => "unknown item type",
    };
}
