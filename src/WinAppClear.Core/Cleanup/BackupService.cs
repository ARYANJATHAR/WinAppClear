using WinAppClear.Core.Models;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;
using Microsoft.Win32;

namespace WinAppClear.Core.Cleanup;

/// <summary>
/// Every removal WinAppClear makes is backed up first: files and folders are moved into the backup folder,
/// registry keys exported to .reg files, registry values and scheduled tasks saved — so they can be restored.
/// </summary>
public static class BackupService
{
    private const string ManifestName = "manifest.json";

    public static BackupSession Begin(string title)
    {
        var session = new BackupSession
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Title = title,
        };
        Directory.CreateDirectory(session.Folder);
        Save(session);
        return session;
    }

    public static void Save(BackupSession session) =>
        JsonStore.Save(Path.Combine(session.Folder, ManifestName), session);

    public static List<BackupSession> List()
    {
        var list = new List<BackupSession>();
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.Backups))
        {
            var manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest)) continue;
            try
            {
                var s = System.Text.Json.JsonSerializer.Deserialize<BackupSession>(File.ReadAllText(manifest), JsonStore.Options);
                if (s is not null && s.Entries.Count > 0) list.Add(s);
            }
            catch (Exception ex)
            {
                Log.Warn($"Bad backup manifest {manifest}: {ex.Message}");
            }
        }
        return list.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public static long SizeOnDisk(BackupSession s) => Directory.Exists(s.Folder) ? PathUtil.DirectorySize(s.Folder) : 0;

    public static void Delete(BackupSession s)
    {
        try
        {
            if (Directory.Exists(s.Folder)) Directory.Delete(s.Folder, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Delete backup {s.Id}: {ex.Message}");
        }
    }

    /// <summary>Moves a file or folder into the backup. Returns the entry or null if it could not be moved.</summary>
    internal static BackupEntry? BackupPath(BackupSession session, LeftoverKind kind, string path, out string? error)
    {
        error = null;
        var target = Path.Combine(session.Folder, "files", $"{session.Entries.Count:D4}_{Path.GetFileName(path)}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            if (Directory.Exists(path))
            {
                if (SameVolume(path, target)) Directory.Move(path, target);
                else
                {
                    CopyDirectory(path, target);
                    Directory.Delete(path, recursive: true);
                }
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Move(path, target);
            }
            else
            {
                error = "already gone";
                return null;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            // A partial cross-volume copy could be left — keep it; restore will skip missing parts.
            return null;
        }

        var entry = new BackupEntry { Kind = kind, OriginalPath = path, BackupFile = target };
        session.Entries.Add(entry);
        Save(session);
        return entry;
    }

    /// <summary>Copies (does not move) a locked file/folder into the backup, for delete-on-reboot cases.</summary>
    internal static BackupEntry? CopyPath(BackupSession session, LeftoverKind kind, string path)
    {
        var target = Path.Combine(session.Folder, "files", $"{session.Entries.Count:D4}_{Path.GetFileName(path)}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            if (Directory.Exists(path)) CopyDirectory(path, target);
            else File.Copy(path, target, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Backup copy {path}: {ex.Message}");
            return null;
        }
        var entry = new BackupEntry { Kind = kind, OriginalPath = path, BackupFile = target, Note = "Deleted at next restart" };
        session.Entries.Add(entry);
        Save(session);
        return entry;
    }

    internal static BackupEntry? BackupRegistryKey(BackupSession session, string keyPath, string view)
    {
        var file = Path.Combine(session.Folder, "registry", $"{session.Entries.Count:D4}.reg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!RegistryPath.Export(keyPath, RegistryPath.ParseView(view), file)) return null;
        var entry = new BackupEntry
        {
            Kind = LeftoverKind.RegistryKey, OriginalPath = keyPath, RegistryView = view, BackupFile = file,
        };
        session.Entries.Add(entry);
        Save(session);
        return entry;
    }

    internal static BackupEntry? BackupRegistryValue(BackupSession session, LeftoverKind kind, string keyPath, string valueName, string view)
    {
        using var key = RegistryPath.Open(keyPath, RegistryPath.ParseView(view));
        if (key is null) return null;
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        var valueKind = key.GetValueKind(valueName);
        var entry = new BackupEntry
        {
            Kind = kind, OriginalPath = keyPath, ValueName = valueName, RegistryView = view,
            ValueKind = valueKind.ToString(), ValueData = SerializeValue(value),
            ValueDataFormat = valueKind == RegistryValueKind.MultiString ? "json" : null,
        };
        session.Entries.Add(entry);
        Save(session);
        return entry;
    }

    internal static BackupEntry BackupTask(BackupSession session, string taskPath)
    {
        var xml = TaskSchedulerService.GetXml(taskPath);
        string? file = null;
        if (xml is not null)
        {
            file = Path.Combine(session.Folder, "tasks", $"{session.Entries.Count:D4}.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        }
        var entry = new BackupEntry
        {
            Kind = LeftoverKind.ScheduledTask, OriginalPath = taskPath, BackupFile = file, Restorable = file is not null,
        };
        session.Entries.Add(entry);
        Save(session);
        return entry;
    }

    internal static BackupEntry? BackupWindowsService(BackupSession session, string serviceName)
    {
        var keyPath = $@"HKLM\{ServiceManager.ServicesKey}\{serviceName}";
        var entry = BackupRegistryKey(session, keyPath, "64");
        if (entry is null) return null;
        // Re-tag as a service so restore knows a reboot is needed for the service to come back.
        session.Entries.Remove(entry);
        var serviceEntry = new BackupEntry
        {
            Kind = LeftoverKind.Service, OriginalPath = serviceName, RegistryView = "64", BackupFile = entry.BackupFile,
            Note = "Restored services come back after a restart.",
        };
        session.Entries.Add(serviceEntry);
        Save(session);
        return serviceEntry;
    }

    internal static BackupEntry AddNonRestorable(BackupSession session, LeftoverKind kind, string path, string note)
    {
        var entry = new BackupEntry { Kind = kind, OriginalPath = path, Restorable = false, Note = note };
        session.Entries.Add(entry);
        Save(session);
        return entry;
    }

    public sealed record RestoreReport(int Restored, int Failed, List<string> Messages);

    /// <summary>Puts everything in a backup back where it was.</summary>
    public static RestoreReport Restore(BackupSession session)
    {
        int ok = 0, failed = 0;
        var messages = new List<string>();
        foreach (var e in Enumerable.Reverse(session.Entries).Where(x => x.Restorable && !x.Restored))
        {
            try
            {
                var success = e.Kind switch
                {
                    LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut => RestorePath(e),
                    LeftoverKind.StartupEntry when e.ValueName is null => RestorePath(e),
                    LeftoverKind.RegistryKey or LeftoverKind.Service =>
                        e.BackupFile is not null && RegistryPath.Import(e.BackupFile, RegistryPath.ParseView(e.RegistryView)),
                    LeftoverKind.RegistryValue or LeftoverKind.StartupEntry => RestoreValue(e),
                    LeftoverKind.ScheduledTask => e.BackupFile is not null && TaskSchedulerService.RegisterFromXml(e.OriginalPath, e.BackupFile),
                    _ => false,
                };
                if (success)
                {
                    e.Restored = true;
                    ok++;
                }
                else
                {
                    failed++;
                    messages.Add($"Could not restore {e.OriginalPath}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"{e.OriginalPath}: {ex.Message}");
            }
        }
        session.Restored = session.Entries.Where(x => x.Restorable).All(x => x.Restored);
        Save(session);
        if (session.Entries.Any(x => x.Kind == LeftoverKind.Service && x.Restored))
            messages.Add("Restart Windows to finish restoring services.");
        Log.Info($"Restore {session.Id}: {ok} restored, {failed} failed");
        return new RestoreReport(ok, failed, messages);
    }

    private static bool RestorePath(BackupEntry e)
    {
        if (e.BackupFile is null) return false;
        if (Directory.Exists(e.BackupFile))
        {
            if (Directory.Exists(e.OriginalPath))
            {
                // Restore non-conflicting files and retain conflicts in the backup for another attempt.
                return RestoreDirectoryContents(e.BackupFile, e.OriginalPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(e.OriginalPath)!);
                if (SameVolume(e.BackupFile, e.OriginalPath)) Directory.Move(e.BackupFile, e.OriginalPath);
                else
                {
                    CopyDirectory(e.BackupFile, e.OriginalPath);
                    Directory.Delete(e.BackupFile, true);
                }
            }
            return true;
        }
        if (File.Exists(e.BackupFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(e.OriginalPath)!);
            if (File.Exists(e.OriginalPath)) return false; // don't overwrite something new
            File.Move(e.BackupFile, e.OriginalPath);
            return true;
        }
        return false;
    }

    private static bool RestoreDirectoryContents(string source, string destination)
    {
        if (File.Exists(destination)) return false;
        Directory.CreateDirectory(destination);
        var complete = true;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target) || Directory.Exists(target))
            {
                complete = false;
                continue;
            }
            File.Move(file, target); // no overwrite, including if a file appears after the check
        }
        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            {
                complete = false;
                continue;
            }
            if (!RestoreDirectoryContents(folder, Path.Combine(destination, Path.GetFileName(folder))))
                complete = false;
        }
        if (complete) Directory.Delete(source, recursive: false);
        return complete;
    }

    private static bool RestoreValue(BackupEntry e)
    {
        if (e.ValueName is null || e.ValueKind is null) return false;
        var (hive, sub) = RegistryPath.Parse(e.OriginalPath);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryPath.ParseView(e.RegistryView));
        using var key = baseKey.CreateSubKey(sub, writable: true);
        var kind = Enum.Parse<RegistryValueKind>(e.ValueKind);
        key.SetValue(e.ValueName, DeserializeValue(kind, e.ValueData, e.ValueDataFormat), kind);
        return true;
    }

    internal static string? SerializeValue(object value) => value switch
    {
        byte[] b => Convert.ToBase64String(b),
        string[] arr => System.Text.Json.JsonSerializer.Serialize(arr),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    internal static object DeserializeValue(RegistryValueKind kind, string? data, string? format = "json") => kind switch
    {
        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(data ?? ""),
        RegistryValueKind.MultiString when format == "json" =>
            System.Text.Json.JsonSerializer.Deserialize<string[]>(data ?? "[]") ?? [],
        RegistryValueKind.MultiString => string.IsNullOrEmpty(data) ? Array.Empty<string>() : data.Split('\0'),
        RegistryValueKind.DWord => unchecked((int)long.Parse(data ?? "0", System.Globalization.CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => long.Parse(data ?? "0", System.Globalization.CultureInfo.InvariantCulture),
        _ => data ?? "",
    };

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
