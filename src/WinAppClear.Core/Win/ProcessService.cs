using System.Diagnostics;
using System.Text;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Win;

public sealed record RunningProcess(int Id, string Name, string? Path);

/// <summary>Finds processes by executable location and kills them; resolves which window belongs to which program.</summary>
public static class ProcessService
{
    public static string? GetImagePath(int pid)
    {
        var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return Native.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    public static List<RunningProcess> GetProcesses()
    {
        var list = new List<RunningProcess>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4) continue;
                list.Add(new RunningProcess(p.Id, p.ProcessName, GetImagePath(p.Id)));
            }
        }
        return list;
    }

    public static List<RunningProcess> FindUnder(string folder) =>
        GetProcesses().Where(p => p.Path is not null && PathUtil.IsStrictlyUnder(p.Path, folder)).ToList();

    public static bool Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.Id == Environment.ProcessId) return false;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(10_000);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // already exited
        }
        catch (Exception ex)
        {
            Log.Warn($"Kill {pid}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Returns the executable of the top-level window under the given screen point.</summary>
    public static (int Pid, string? Path) ProcessAtScreenPoint(int x, int y)
    {
        var hwnd = Native.WindowFromPoint(new Native.Point { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return (0, null);
        var root = Native.GetAncestor(hwnd, 2 /* GA_ROOT */);
        if (root != IntPtr.Zero) hwnd = root;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return (pid, pid > 0 ? GetImagePath(pid) : null);
    }

    public static (int X, int Y) CursorPosition()
    {
        Native.GetCursorPos(out var p);
        return (p.X, p.Y);
    }
}
