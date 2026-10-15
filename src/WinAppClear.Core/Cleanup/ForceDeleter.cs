using WinAppClear.Core.Safety;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Cleanup;

public sealed record ForceDeleteResult(string Path, bool Deleted, bool ScheduledForReboot, string? Message);

/// <summary>
/// Deletes stubborn files without a kernel driver: clears read-only attributes, closes the processes that
/// lock them (when allowed), and falls back to deleting at the next restart.
/// </summary>
public static class ForceDeleter
{
    public static bool ScheduleDeleteOnReboot(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // Files first, then folders deepest-first: Windows only removes empty folders at boot.
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    Native.MoveFileEx(file, null, Native.MovefileDelayUntilReboot);
                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                    Native.MoveFileEx(dir, null, Native.MovefileDelayUntilReboot);
            }
            return Native.MoveFileEx(path, null, Native.MovefileDelayUntilReboot);
        }
        catch (Exception ex)
        {
            Log.Warn($"Schedule delete {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Force-deletes a path. When <paramref name="killLockers"/> is set, processes holding it are closed first.</summary>
    public static ForceDeleteResult Delete(string path, bool killLockers, bool sendToRecycleBin = false)
    {
        var refusal = SafetyGuard.ProtectedPathReason(path);
        if (refusal is not null) return new(path, false, false, $"Protected: {refusal}");
        if (!File.Exists(path) && !Directory.Exists(path)) return new(path, true, false, "Already gone");

        if (killLockers)
        {
            foreach (var p in FileLockFinder.WhoIsLocking(FileLockFinder.ExpandFiles([path])))
            {
                if (p.Pid != Environment.ProcessId) ProcessService.Kill(p.Pid);
            }
            // Also processes started from inside the folder (they may not have the file open right now).
            if (Directory.Exists(path))
                foreach (var p in ProcessService.FindUnder(path)) ProcessService.Kill(p.Id);
        }

        try
        {
            ClearAttributes(path);
            if (sendToRecycleBin)
            {
                if (RecycleBin.Send(path)) return new(path, true, false, "Moved to the Recycle Bin");
            }
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);

            if (!File.Exists(path) && !Directory.Exists(path)) return new(path, true, false, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Force delete {path}: {ex.Message}");
        }

        return ScheduleDeleteOnReboot(path)
            ? new(path, false, true, "Still in use — it will be deleted when Windows restarts")
            : new(path, false, false, "Could not delete or schedule deletion (are you running as administrator?)");
    }

    private static void ClearAttributes(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true, IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best effort */ }
                }
            }
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        catch
        {
            // best effort
        }
    }
}
