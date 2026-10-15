using WinAppClear.Core.Util;

namespace WinAppClear.Core.Cleanup;

/// <summary>Deletes backups older than the retention period so they don't pile up.</summary>
public static class BackupMaintenance
{
    public static int PruneOld(int retentionDays)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var removed = 0;
        foreach (var s in BackupService.List().Where(s => s.CreatedAt < cutoff))
        {
            BackupService.Delete(s);
            removed++;
        }
        // Empty session folders (nothing was backed up).
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.Backups))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any(e => !e.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)) &&
                    Directory.GetCreationTime(dir) < DateTime.Now.AddHours(-1))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // ignore
            }
        }
        if (removed > 0) Log.Info($"Pruned {removed} old backup(s)");
        return removed;
    }
}
