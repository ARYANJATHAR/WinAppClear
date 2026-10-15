using WinAppClear.Core.Models;
using WinAppClear.Core.Safety;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Cleanup;

public enum CleanupStatus
{
    Removed,
    ScheduledForReboot,
    Skipped,
    Failed,
}

public sealed record CleanupItemResult(LeftoverItem Item, CleanupStatus Status, string? Message);

public sealed record CleanupReport(string BackupId, List<CleanupItemResult> Results)
{
    public int Removed => Results.Count(r => r.Status == CleanupStatus.Removed);
    public int Scheduled => Results.Count(r => r.Status == CleanupStatus.ScheduledForReboot);
    public int Failed => Results.Count(r => r.Status is CleanupStatus.Failed);
    public int Skipped => Results.Count(r => r.Status == CleanupStatus.Skipped);
    public List<LeftoverItem> NotRemoved => Results.Where(r => r.Status is CleanupStatus.Failed or CleanupStatus.Skipped)
        .Select(r => r.Item).ToList();
}

/// <summary>Removes selected leftovers: safety check → backup → delete. Nothing is deleted without a backup.</summary>
public static class CleanupExecutor
{
    public static CleanupReport Execute(string title, IReadOnlyList<LeftoverItem> items, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var session = BackupService.Begin(title);
        var results = new List<CleanupItemResult>();

        // Stop services and remove system items first, then registry, then files (files may be held by services).
        var ordered = items.OrderBy(i => i.Kind switch
        {
            LeftoverKind.Service => 0,
            LeftoverKind.ScheduledTask => 1,
            LeftoverKind.StartupEntry => 2,
            LeftoverKind.FirewallRule => 3,
            LeftoverKind.RegistryValue => 4,
            LeftoverKind.RegistryKey => 5,
            LeftoverKind.Shortcut => 6,
            LeftoverKind.File => 7,
            _ => 8,
        }).ToList();

        foreach (var item in ordered)
        {
            if (ct.IsCancellationRequested)
            {
                results.Add(new CleanupItemResult(item, CleanupStatus.Skipped, "Cancelled"));
                continue;
            }

            var refusal = SafetyGuard.Check(item);
            if (refusal is not null)
            {
                Log.Warn($"Refused to remove {item}: {refusal}");
                results.Add(new CleanupItemResult(item, CleanupStatus.Skipped, $"Protected: {refusal}"));
                continue;
            }

            progress?.Report($"Removing {item.KindLabel.ToLowerInvariant()}: {item.DisplayPath}");
            try
            {
                results.Add(RemoveOne(session, item));
            }
            catch (Exception ex)
            {
                Log.Error($"Remove {item}", ex);
                results.Add(new CleanupItemResult(item, CleanupStatus.Failed, ex.Message));
            }
        }

        if (session.Entries.Count == 0) BackupService.Delete(session);
        var report = new CleanupReport(session.Id, results);
        Log.Info($"Cleanup '{title}': {report.Removed} removed, {report.Scheduled} on reboot, {report.Failed} failed, {report.Skipped} skipped");
        return report;
    }

    private static CleanupItemResult RemoveOne(BackupSession session, LeftoverItem item)
    {
        switch (item.Kind)
        {
            case LeftoverKind.Folder:
            case LeftoverKind.File:
            case LeftoverKind.Shortcut:
            case LeftoverKind.StartupEntry when item.ValueName is null:
                return RemovePath(session, item);

            case LeftoverKind.RegistryKey:
            {
                var view = RegistryPath.ParseView(item.RegistryView);
                if (!RegistryPath.KeyExists(item.Path, view)) return new(item, CleanupStatus.Removed, "Already gone");
                if (BackupService.BackupRegistryKey(session, item.Path, item.RegistryView ?? "64") is null)
                    return new(item, CleanupStatus.Failed, "Could not back up the key, so it was left alone");
                RegistryPath.DeleteKeyTree(item.Path, view);
                return new(item, CleanupStatus.Removed, null);
            }

            case LeftoverKind.RegistryValue:
            case LeftoverKind.StartupEntry:
            {
                var view = RegistryPath.ParseView(item.RegistryView);
                if (!RegistryPath.ValueExists(item.Path, item.ValueName!, view)) return new(item, CleanupStatus.Removed, "Already gone");
                if (BackupService.BackupRegistryValue(session, item.Kind, item.Path, item.ValueName!, item.RegistryView ?? "64") is null)
                    return new(item, CleanupStatus.Failed, "Could not back up the value, so it was left alone");
                using var key = RegistryPath.Open(item.Path, view, writable: true);
                key?.DeleteValue(item.ValueName!, throwOnMissingValue: false);
                return new(item, CleanupStatus.Removed, null);
            }

            case LeftoverKind.Service:
            {
                if (!ServiceManager.Exists(item.Path)) return new(item, CleanupStatus.Removed, "Already gone");
                var binary = ServiceManager.GetServices(includeDrivers: true).FirstOrDefault(s =>
                    s.Name.Equals(item.Path, StringComparison.OrdinalIgnoreCase))?.BinaryPath;
                if (binary is not null && PathUtil.IsUnderOrEqual(binary, KnownPaths.Windows))
                    return new(item, CleanupStatus.Skipped, "Service runs from the Windows folder — left alone");
                if (BackupService.BackupWindowsService(session, item.Path) is null)
                    return new(item, CleanupStatus.Failed, "Could not back up the service, so it was left alone");
                return ServiceManager.Delete(item.Path)
                    ? new(item, CleanupStatus.Removed, null)
                    : new(item, CleanupStatus.Failed, "Windows refused to delete the service");
            }

            case LeftoverKind.ScheduledTask:
            {
                if (!TaskSchedulerService.Exists(item.Path)) return new(item, CleanupStatus.Removed, "Already gone");
                if (!BackupService.BackupTask(session, item.Path).Restorable)
                    return new(item, CleanupStatus.Failed, "Could not back up the task, so it was left alone");
                return TaskSchedulerService.Delete(item.Path)
                    ? new(item, CleanupStatus.Removed, null)
                    : new(item, CleanupStatus.Failed, "Could not delete the task");
            }

            case LeftoverKind.FirewallRule:
            {
                BackupService.AddNonRestorable(session, item.Kind, item.Path, $"Firewall rule for {item.Detail}");
                return FirewallService.Remove(item.Path, item.Detail ?? "")
                    ? new(item, CleanupStatus.Removed, null)
                    : new(item, CleanupStatus.Failed, "Could not remove the firewall rule");
            }

            default:
                return new(item, CleanupStatus.Skipped, "Unsupported item");
        }
    }

    private static CleanupItemResult RemovePath(BackupSession session, LeftoverItem item)
    {
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) return new(item, CleanupStatus.Removed, "Already gone");

        if (BackupService.BackupPath(session, item.Kind, item.Path, out var error) is not null)
            return new(item, CleanupStatus.Removed, null);

        // Do not schedule any deletion unless the complete backup copy succeeded.
        if (BackupService.CopyPath(session, item.Kind, item.Path) is null)
            return new(item, CleanupStatus.Failed, $"Could not back up the item, so it was left alone: {error}");
        var lockers = FileLockFinder.WhoIsLocking(FileLockFinder.ExpandFiles([item.Path], 500));
        var lockedBy = lockers.Count > 0 ? $" (in use by {string.Join(", ", lockers.Select(l => l.Name).Distinct())})" : "";
        if (ForceDeleter.ScheduleDeleteOnReboot(item.Path))
            return new(item, CleanupStatus.ScheduledForReboot, $"In use{lockedBy}; will be removed at the next restart");
        return new(item, CleanupStatus.Failed, $"{error}{lockedBy}");
    }
}
