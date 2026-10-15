using System.Runtime.InteropServices;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Win;

public sealed record ScheduledTaskInfo(string Path, string Name, bool Enabled, string? ExecutablePath, string? Arguments, string? Author)
{
    public string Command => string.IsNullOrEmpty(Arguments) ? ExecutablePath ?? "" : $"{ExecutablePath} {Arguments}";
}

/// <summary>Reads and changes scheduled tasks through the Task Scheduler COM API.</summary>
public static class TaskSchedulerService
{
    private const int TaskEnumHidden = 1;
    private const int TaskActionExec = 0;

    public static List<ScheduledTaskInfo> GetTasks(bool includeMicrosoft = false)
    {
        var list = new List<ScheduledTaskInfo>();
        dynamic? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return list;
            service = Activator.CreateInstance(type)!;
            service.Connect();
            Walk(service.GetFolder("\\"), list, includeMicrosoft);
        }
        catch (Exception ex)
        {
            Log.Warn($"Task enumeration failed: {ex.Message}");
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
        return list;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> list, bool includeMicrosoft)
    {
        string folderPath = folder.Path;
        if (!includeMicrosoft && folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            foreach (dynamic task in folder.GetTasks(TaskEnumHidden))
            {
                try
                {
                    string? exe = null, args = null, author = null;
                    dynamic def = task.Definition;
                    try { author = def.RegistrationInfo.Author; } catch { /* optional */ }
                    foreach (dynamic action in def.Actions)
                    {
                        if ((int)action.Type != TaskActionExec) continue;
                        exe = PathUtil.ExtractExecutablePath((string)action.Path) ?? (string)action.Path;
                        args = action.Arguments;
                        break;
                    }
                    list.Add(new ScheduledTaskInfo((string)task.Path, (string)task.Name, (bool)task.Enabled, exe, args, author));
                }
                catch
                {
                    // Task without readable definition — skip.
                }
            }
        }
        catch
        {
            // Folder not readable without elevation.
        }

        try
        {
            foreach (dynamic sub in folder.GetFolders(0)) Walk(sub, list, includeMicrosoft);
        }
        catch
        {
            // Ignore unreadable sub-folders.
        }
    }

    private static T WithTask<T>(string taskPath, Func<dynamic, dynamic, T> action, T fallback)
    {
        dynamic? service = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            var folderPath = taskPath[..Math.Max(1, taskPath.LastIndexOf('\\'))];
            dynamic folder = service.GetFolder(folderPath);
            dynamic task = folder.GetTask(taskPath);
            return action(folder, task);
        }
        catch (Exception ex)
        {
            Log.Warn($"Task operation on {taskPath} failed: {ex.Message}");
            return fallback;
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }

    public static bool Exists(string taskPath) => WithTask(taskPath, (_, _) => true, false);

    public static string? GetXml(string taskPath) => WithTask<string?>(taskPath, (_, t) => (string)t.Xml, null);

    public static bool SetEnabled(string taskPath, bool enabled) =>
        WithTask(taskPath, (_, t) =>
        {
            t.Enabled = enabled;
            return true;
        }, false);

    public static bool Delete(string taskPath) =>
        WithTask(taskPath, (f, t) =>
        {
            f.DeleteTask((string)t.Name, 0);
            return true;
        }, false);

    /// <summary>Re-creates a task from exported XML (used when restoring a backup).</summary>
    public static bool RegisterFromXml(string taskPath, string xmlFile)
    {
        var r = ProcessRunner.Run(ProcessRunner.SystemTool("schtasks.exe"), $"/Create /TN \"{taskPath}\" /XML \"{xmlFile}\" /F");
        if (!r.Ok) Log.Warn($"schtasks create {taskPath}: {r.Output} {r.Error}");
        return r.Ok;
    }
}
