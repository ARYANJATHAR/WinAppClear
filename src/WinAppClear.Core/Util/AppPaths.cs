namespace WinAppClear.Core.Util;

/// <summary>Where WinAppClear keeps its own data (backups, history, logs, settings).</summary>
public static class AppPaths
{
    private static string? _rootOverride;

    /// <summary>Lets tests redirect all app data into a temp folder.</summary>
    public static void OverrideRoot(string? root) => _rootOverride = root;

    public static string Root
    {
        get
        {
            var root = _rootOverride ?? ResolveDefaultRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            Directory.CreateDirectory(root);
            return root;
        }
    }

    // Existing backup manifests contain absolute paths. Keep using the legacy data
    // directory when upgrading rather than moving it and breaking those references.
    internal static string ResolveDefaultRoot(string localAppData)
    {
        var current = Path.Combine(localAppData, "WinAppClear");
        var legacy = Path.Combine(localAppData, "ClearOut");
        return Directory.Exists(current) || !Directory.Exists(legacy) ? current : legacy;
    }

    public static string Backups => Ensure(Path.Combine(Root, "Backups"));
    public static string InstallLogs => Ensure(Path.Combine(Root, "InstallLogs"));
    public static string Logs => Ensure(Path.Combine(Root, "Logs"));
    public static string Snapshots => Ensure(Path.Combine(Root, "Snapshots"));
    public static string HistoryFile => Path.Combine(Root, "history.json");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string HibernationFile => Path.Combine(Root, "hibernation.json");
    public static string DisabledItemsFile => Path.Combine(Root, "disabled-items.json");
    public static string UserBlocklistFile => Path.Combine(Root, "blocklist.json");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
