namespace WinAppClear.Core.Util;

/// <summary>User preferences, saved to settings.json.</summary>
public sealed class AppSettings
{
    public bool CreateRestorePoint { get; set; } = true;
    public bool QuietUninstallByDefault { get; set; }
    public bool PreselectOnlyHighConfidence { get; set; } = true;
    public bool AutoScanLeftovers { get; set; } = true;
    public int ShredPasses { get; set; } = 1;
    public int BackupRetentionDays { get; set; } = 30;
    public DateTime? LastHealthScan { get; set; }
    public bool ShowSystemComponents { get; set; }

    private static AppSettings? _current;

    public static AppSettings Current => _current ??= JsonStore.Load<AppSettings>(AppPaths.SettingsFile);

    public void Save() => JsonStore.Save(AppPaths.SettingsFile, this);
}
