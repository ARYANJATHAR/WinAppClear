using WinAppClear.Core.Inventory;
using WinAppClear.Core.Models;
using WinAppClear.Core.Uninstall;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Monitor;

public sealed class InstallLog
{
    public string Id { get; init; } = $"{DateTime.Now:yyyyMMdd-HHmmss}";
    public string Name { get; set; } = "Unnamed install";
    public DateTime Started { get; init; }
    public DateTime Finished { get; init; }
    public string? InstallerPath { get; init; }
    public List<string> NewProgramNames { get; init; } = [];
    public List<string> AddedFolders { get; init; } = [];
    public List<string> AddedFiles { get; init; } = [];
    public List<string> ModifiedFiles { get; init; } = [];
    /// <summary>"view|HKLM\path" entries.</summary>
    public List<string> AddedKeys { get; init; } = [];
    /// <summary>"view|HKLM\path\0valueName" entries for values added to keys that existed before.</summary>
    public List<string> AddedValues { get; init; } = [];
    public List<string> ModifiedValues { get; init; } = [];
    public List<string> AddedServices { get; init; } = [];
    public List<string> AddedTasks { get; init; } = [];
    public bool Removed { get; set; }

    public int TotalChanges => AddedFolders.Count + AddedFiles.Count + AddedKeys.Count + AddedValues.Count + AddedServices.Count + AddedTasks.Count;
}

/// <summary>
/// Install Monitor without a kernel driver: snapshot before → user installs → snapshot after → diff.
/// The resulting log lets WinAppClear remove exactly what the installer added.
/// </summary>
public static class InstallMonitor
{
    private static string PendingFile => Path.Combine(AppPaths.Snapshots, "pending.snapshot");
    private static string PendingInfoFile => Path.Combine(AppPaths.Snapshots, "pending.json");

    public sealed class PendingInfo
    {
        public DateTime Started { get; set; }
        public string? InstallerPath { get; set; }
        public string Name { get; set; } = "";
    }

    public static PendingInfo? Pending => File.Exists(PendingInfoFile) && File.Exists(PendingFile)
        ? JsonStore.Load<PendingInfo>(PendingInfoFile)
        : null;

    /// <summary>Takes and saves the "before" snapshot.</summary>
    public static async Task StartAsync(string name, string? installerPath, IProgress<string>? progress, CancellationToken ct)
    {
        var snap = await Task.Run(() => SystemSnapshot.Take(progress, ct), ct);
        progress?.Report("Saving snapshot…");
        await Task.Run(() => snap.Save(PendingFile), ct);
        JsonStore.Save(PendingInfoFile, new PendingInfo { Started = snap.Taken, InstallerPath = installerPath, Name = name });
        Log.Info($"Install monitor started for '{name}'");
    }

    /// <summary>Launches the installer and waits for it (and its child processes) to finish.</summary>
    public static Task<int?> RunInstallerAsync(string installerPath, CancellationToken ct)
    {
        var cmd = installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
            ? new CommandLine(UninstallCommand.MsiExecPath, $"/i \"{installerPath}\"")
            : new CommandLine(installerPath, "");
        return UninstallRunner.RunAndWaitForTreeAsync(cmd, ct, Path.GetDirectoryName(installerPath));
    }

    /// <summary>Takes the "after" snapshot, compares, and saves the install log.</summary>
    public static async Task<InstallLog> FinishAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var info = Pending ?? throw new InvalidOperationException("No install is being monitored.");
        progress?.Report("Loading the first snapshot…");
        var before = await Task.Run(() => SystemSnapshot.Load(PendingFile), ct);
        var after = await Task.Run(() => SystemSnapshot.Take(progress, ct), ct);
        progress?.Report("Comparing…");
        var log = Diff(before, after, info.Name, info.InstallerPath);
        if (log.NewProgramNames.Count > 0 && (string.IsNullOrWhiteSpace(info.Name) || info.Name == "Unnamed install"))
            log.Name = log.NewProgramNames[0];
        InstallLogStore.Save(log);
        Cancel();
        Log.Info($"Install monitor finished '{log.Name}': {log.TotalChanges} changes");
        return log;
    }

    public static void Cancel()
    {
        try
        {
            if (File.Exists(PendingFile)) File.Delete(PendingFile);
            if (File.Exists(PendingInfoFile)) File.Delete(PendingInfoFile);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cancel monitor: {ex.Message}");
        }
    }

    public static InstallLog Diff(SystemSnapshot before, SystemSnapshot after, string name, string? installerPath)
    {
        var addedDirs = after.Directories.Where(d => !before.Directories.Contains(d)).ToList();
        var topDirs = TopMost(addedDirs);

        var addedFiles = after.Files.Keys.Where(f => !before.Files.ContainsKey(f))
            .Where(f => !topDirs.Any(d => PathUtil.IsStrictlyUnder(f, d))).ToList();
        var modifiedFiles = after.Files.Where(kv => before.Files.TryGetValue(kv.Key, out var old) && old != kv.Value)
            .Select(kv => kv.Key).ToList();

        var addedKeys = after.RegistryKeys.Where(k => !before.RegistryKeys.Contains(k)).ToList();
        var topKeys = TopMost(addedKeys);

        var addedValues = new List<string>();
        var modifiedValues = new List<string>();
        foreach (var (k, hash) in after.RegistryValues)
        {
            var keyPart = k[..k.IndexOf('\0')];
            if (topKeys.Any(t => keyPart.Equals(t, StringComparison.OrdinalIgnoreCase) ||
                                 keyPart.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!before.RegistryValues.TryGetValue(k, out var oldHash)) addedValues.Add(k);
            else if (oldHash != hash) modifiedValues.Add(k);
        }

        const string servicesPrefix = @"64|HKLM\SYSTEM\CurrentControlSet\Services\";
        var services = topKeys.Where(k => k.StartsWith(servicesPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[servicesPrefix.Length..]).Where(s => !s.Contains('\\')).ToList();

        var tasksRoot = PathUtil.TrimEnd(KnownPaths.SystemTasks);
        var tasks = addedFiles.Concat(topDirs.SelectMany(d => after.Files.Keys.Where(f => PathUtil.IsStrictlyUnder(f, d))))
            .Where(f => PathUtil.IsStrictlyUnder(f, tasksRoot))
            .Select(f => f[tasksRoot.Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        const string uninstallMarker = @"\Microsoft\Windows\CurrentVersion\Uninstall\";
        var newPrograms = new List<string>();
        foreach (var k in topKeys.Where(k => k.Contains(uninstallMarker, StringComparison.OrdinalIgnoreCase)))
        {
            var valueKey = $"{k}\0DisplayName";
            if (after.RegistryValues.ContainsKey(valueKey))
            {
                var keyPath = k[(k.IndexOf('|') + 1)..];
                using var key = RegistryPath.Open(keyPath, RegistryPath.ParseView(k[..k.IndexOf('|')]));
                if (key?.GetValue("DisplayName") is string dn) newPrograms.Add(dn);
            }
        }

        return new InstallLog
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Unnamed install" : name,
            Started = before.Taken,
            Finished = after.Taken,
            InstallerPath = installerPath,
            NewProgramNames = newPrograms,
            AddedFolders = topDirs.Where(d => !PathUtil.IsUnderOrEqual(d, tasksRoot)).OrderBy(x => x).ToList(),
            AddedFiles = addedFiles.Where(f => !PathUtil.IsStrictlyUnder(f, tasksRoot)).OrderBy(x => x).ToList(),
            ModifiedFiles = modifiedFiles.OrderBy(x => x).ToList(),
            AddedKeys = topKeys.Where(k => !k.StartsWith(servicesPrefix, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x).ToList(),
            AddedValues = addedValues.OrderBy(x => x).ToList(),
            ModifiedValues = modifiedValues.OrderBy(x => x).ToList(),
            AddedServices = services,
            AddedTasks = tasks,
        };
    }

    /// <summary>Keeps only items whose parent path is not also in the list.</summary>
    private static List<string> TopMost(List<string> items)
    {
        var set = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        return items.Where(i =>
        {
            // Walk up the parents and see if any parent is also in the set.
            var current = i;
            while (true)
            {
                var cut = current.LastIndexOf('\\');
                if (cut <= 0) return true;
                current = current[..cut];
                if (set.Contains(current)) return false;
            }
        }).ToList();
    }

    /// <summary>Turns an install log into removable items (everything the installer added).</summary>
    public static List<LeftoverItem> ToLeftovers(InstallLog log)
    {
        var items = new List<LeftoverItem>();
        foreach (var s in log.AddedServices)
            items.Add(new LeftoverItem { Kind = LeftoverKind.Service, Path = s, Confidence = Confidence.High, Reason = "Service added by the installer" });
        foreach (var t in log.AddedTasks)
            items.Add(new LeftoverItem { Kind = LeftoverKind.ScheduledTask, Path = t, Confidence = Confidence.High, Reason = "Task added by the installer" });
        foreach (var d in log.AddedFolders.Where(Directory.Exists))
            items.Add(new LeftoverItem { Kind = LeftoverKind.Folder, Path = d, Confidence = Confidence.High, Reason = "Folder added by the installer" });
        foreach (var f in log.AddedFiles.Where(File.Exists))
            items.Add(new LeftoverItem
            {
                Kind = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? LeftoverKind.Shortcut : LeftoverKind.File,
                Path = f, Confidence = Confidence.High, Reason = "File added by the installer",
            });
        foreach (var k in log.AddedKeys)
        {
            var bar = k.IndexOf('|');
            var view = k[..bar];
            var path = k[(bar + 1)..];
            if (!RegistryPath.KeyExists(path, RegistryPath.ParseView(view))) continue;
            items.Add(new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = path, RegistryView = view, Confidence = Confidence.High, Reason = "Registry key added by the installer" });
        }
        foreach (var v in log.AddedValues)
        {
            var bar = v.IndexOf('|');
            var nul = v.IndexOf('\0');
            var view = v[..bar];
            var path = v[(bar + 1)..nul];
            var valueName = v[(nul + 1)..];
            if (!RegistryPath.ValueExists(path, valueName, RegistryPath.ParseView(view))) continue;
            var isRun = path.EndsWith(@"\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase);
            items.Add(new LeftoverItem
            {
                Kind = isRun ? LeftoverKind.StartupEntry : LeftoverKind.RegistryValue, Path = path, ValueName = valueName,
                RegistryView = view, Confidence = Confidence.High, Reason = "Registry value added by the installer",
            });
        }
        foreach (var item in items.Where(i => i.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut))
        {
            item.SizeBytes = item.Kind == LeftoverKind.Folder ? PathUtil.DirectorySize(item.Path) : new FileInfo(item.Path).Length;
        }
        return items.Where(i => Safety.SafetyGuard.Check(i) is null).ToList();
    }

    /// <summary>Finds the installed program(s) this log created, so their uninstaller can run first.</summary>
    public static List<InstalledProgram> ProgramsFromLog(InstallLog log)
    {
        var keys = log.AddedKeys.Select(k => k[(k.IndexOf('|') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ProgramInventory.GetInstalledPrograms()
            .Where(p => keys.Contains(p.RegistryKeyPath) || log.NewProgramNames.Contains(p.DisplayName))
            .ToList();
    }
}

/// <summary>Saved install logs ("Logged Programs").</summary>
public static class InstallLogStore
{
    public static void Save(InstallLog log) => JsonStore.Save(Path.Combine(AppPaths.InstallLogs, $"{log.Id}.json"), log);

    public static List<InstallLog> All()
    {
        var list = new List<InstallLog>();
        foreach (var f in Directory.EnumerateFiles(AppPaths.InstallLogs, "*.json"))
        {
            try
            {
                var log = System.Text.Json.JsonSerializer.Deserialize<InstallLog>(File.ReadAllText(f), JsonStore.Options);
                if (log is not null) list.Add(log);
            }
            catch (Exception ex)
            {
                Log.Warn($"Bad install log {f}: {ex.Message}");
            }
        }
        return list.OrderByDescending(l => l.Finished).ToList();
    }

    public static void Delete(InstallLog log)
    {
        var f = Path.Combine(AppPaths.InstallLogs, $"{log.Id}.json");
        if (File.Exists(f)) File.Delete(f);
    }
}
