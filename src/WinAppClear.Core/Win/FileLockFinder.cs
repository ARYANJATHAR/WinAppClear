using System.Text;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Win;

public sealed record LockingProcess(int Pid, string Name, string? Path);

/// <summary>Uses the Windows Restart Manager to find which processes keep files open.</summary>
public static class FileLockFinder
{
    public static List<LockingProcess> WhoIsLocking(IReadOnlyList<string> files)
    {
        var result = new List<LockingProcess>();
        if (files.Count == 0) return result;

        var key = new StringBuilder(Guid.NewGuid().ToString());
        if (Native.RmStartSession(out var session, 0, key) != 0) return result;
        try
        {
            // Restart Manager handles a few thousand files per call comfortably.
            foreach (var chunk in files.Chunk(1000))
            {
                if (Native.RmRegisterResources(session, (uint)chunk.Length, chunk, 0, null, 0, null) != 0) continue;
            }

            uint needed = 0, count = 0, reasons = 0;
            var rc = Native.RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc == 234 /* ERROR_MORE_DATA */ && needed > 0)
            {
                var info = new Native.RmProcessInfo[needed];
                count = needed;
                rc = Native.RmGetList(session, out needed, ref count, info, ref reasons);
                if (rc == 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var pid = info[i].Process.dwProcessId;
                        result.Add(new LockingProcess(pid, info[i].strAppName, ProcessService.GetImagePath(pid)));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Restart Manager query failed: {ex.Message}");
        }
        finally
        {
            Native.RmEndSession(session);
        }
        return result.DistinctBy(r => r.Pid).ToList();
    }

    /// <summary>Collects up to <paramref name="limit"/> files from a mix of files and folders.</summary>
    public static List<string> ExpandFiles(IEnumerable<string> paths, int limit = 5000)
    {
        var list = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) list.Add(p);
            else if (Directory.Exists(p))
            {
                try
                {
                    list.AddRange(Directory.EnumerateFiles(p, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                    }).Take(limit - list.Count));
                }
                catch
                {
                    // Ignore.
                }
            }
            if (list.Count >= limit) break;
        }
        return list;
    }
}
