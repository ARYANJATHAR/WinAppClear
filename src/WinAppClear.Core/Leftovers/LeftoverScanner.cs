using WinAppClear.Core.Inventory;
using WinAppClear.Core.Models;
using WinAppClear.Core.Safety;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;
using Microsoft.Win32;

namespace WinAppClear.Core.Leftovers;

/// <summary>What to look for: a program (after uninstall) or a bare folder (force uninstall).</summary>
public sealed record ScanTarget(
    string DisplayName,
    string? Publisher,
    string? InstallFolder,
    string? UninstallKeyPath,
    RegistryView UninstallKeyView,
    string? MsiProductCode,
    string? ProgramId = null)
{
    public static ScanTarget FromProgram(InstalledProgram p) => new(
        p.DisplayName, p.Publisher, ProgramInventory.GuessInstallFolder(p), p.RegistryKeyPath, p.View, p.MsiProductCode, p.Id);

    public static ScanTarget FromFolder(string folder, string? displayName = null) => new(
        displayName ?? Path.GetFileName(PathUtil.TrimEnd(folder)), null, PathUtil.Normalize(folder), null,
        RegistryView.Registry64, null);
}

public sealed class LeftoverScanOptions
{
    public required IReadOnlyList<string> FolderRoots { get; init; }
    public required IReadOnlyList<(RegistryHive Hive, RegistryView View, string SubKey)> RegistryRoots { get; init; }
    public bool IncludeSystemItems { get; init; } = true;
    public bool MeasureSizes { get; init; } = true;

    public static LeftoverScanOptions Default => new()
    {
        FolderRoots =
        [
            KnownPaths.ProgramFiles, KnownPaths.ProgramFilesX86, KnownPaths.CommonProgramFiles,
            KnownPaths.CommonProgramFilesX86, KnownPaths.ProgramData, KnownPaths.AppData, KnownPaths.LocalAppData,
            KnownPaths.LocalLow, Path.Combine(KnownPaths.LocalAppData, "Programs"), KnownPaths.StartMenuPrograms,
            KnownPaths.CommonStartMenuPrograms, KnownPaths.UserProfile, KnownPaths.Documents,
        ],
        RegistryRoots =
        [
            (RegistryHive.CurrentUser, RegistryView.Registry64, "Software"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE"),
        ],
    };
}

/// <summary>
/// Finds files, folders, registry keys and system items a program left behind.
/// Matching is deliberately conservative; every result is re-checked by <see cref="SafetyGuard"/>.
/// </summary>
public sealed class LeftoverScanner
{
    private readonly LeftoverScanOptions _options;
    private readonly IReadOnlyList<InstalledProgram> _otherPrograms;
    private readonly SystemFootprint _footprint;

    public LeftoverScanner(LeftoverScanOptions? options = null, IReadOnlyList<InstalledProgram>? installedPrograms = null,
        SystemFootprint? footprint = null)
    {
        _options = options ?? LeftoverScanOptions.Default;
        _otherPrograms = installedPrograms ?? [];
        _footprint = footprint ?? new SystemFootprint();
    }

    public List<LeftoverItem> Scan(ScanTarget target, CancellationToken ct = default)
    {
        var others = _otherPrograms.Where(p => p.Id != target.ProgramId).ToList();
        var otherFolders = others.Select(ProgramInventory.GuessInstallFolder).OfType<string>().ToList();
        var otherKeys = others.Select(p => NameMatcher.Normalize(p.DisplayName)).Where(k => !NameMatcher.IsGeneric(k)).ToHashSet();

        var nameKeys = new HashSet<string>(StringComparer.Ordinal);
        AddKey(nameKeys, NameMatcher.Normalize(target.DisplayName));
        if (target.InstallFolder is not null)
        {
            AddKey(nameKeys, NameMatcher.Normalize(Path.GetFileName(target.InstallFolder)));
        }
        var publisherKey = NameMatcher.NormalizePublisher(target.Publisher);
        if (NameMatcher.IsGeneric(publisherKey)) publisherKey = "";

        var items = new Dictionary<string, LeftoverItem>();
        void Add(LeftoverItem item)
        {
            if (SafetyGuard.Check(item) is not null) return;
            items.TryAdd(item.Key, item);
        }

        // 1. The install folder itself.
        if (target.InstallFolder is not null && Directory.Exists(target.InstallFolder) &&
            !otherFolders.Any(f => PathUtil.IsUnderOrEqual(f, target.InstallFolder)))
        {
            Add(new LeftoverItem
            {
                Kind = LeftoverKind.Folder, Path = target.InstallFolder, Confidence = Confidence.High,
                Reason = "Program install folder",
            });
        }

        // 2. Folders named after the program (or publisher\program).
        foreach (var root in _options.FolderRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            ScanFolderRoot(root, nameKeys, publisherKey, otherFolders, otherKeys, Add);
        }

        // 3. Registry keys named after the program (or publisher\program).
        foreach (var (hive, view, sub) in _options.RegistryRoots)
        {
            ct.ThrowIfCancellationRequested();
            ScanRegistryRoot(hive, view, sub, nameKeys, publisherKey, otherKeys, Add);
        }

        // 4. Leftover uninstall entry and Windows Installer registration.
        if (target.UninstallKeyPath is not null && RegistryPath.KeyExists(target.UninstallKeyPath, target.UninstallKeyView))
        {
            Add(new LeftoverItem
            {
                Kind = LeftoverKind.RegistryKey, Path = target.UninstallKeyPath,
                RegistryView = RegistryPath.ViewName(target.UninstallKeyView), Confidence = Confidence.High,
                Reason = "Uninstall entry still registered",
            });
        }
        if (RegistryPath.PackGuid(target.MsiProductCode) is { } packed)
        {
            foreach (var path in new[]
                     {
                         $@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}",
                         $@"HKLM\SOFTWARE\Classes\Installer\Features\{packed}",
                         $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{packed}",
                     })
            {
                if (RegistryPath.KeyExists(path, RegistryView.Registry64))
                {
                    Add(new LeftoverItem
                    {
                        Kind = LeftoverKind.RegistryKey, Path = path, RegistryView = "64", Confidence = Confidence.High,
                        Reason = "Windows Installer registration",
                    });
                }
            }
        }

        // 5. Services, scheduled tasks, startup entries, firewall rules and shortcuts tied to the program.
        if (_options.IncludeSystemItems)
        {
            ct.ThrowIfCancellationRequested();
            ScanSystemItems(target, nameKeys, otherFolders, Add);
        }

        AddStartMenuFolders(items, Add);
        var result = Collapse(items.Values.ToList());
        if (_options.MeasureSizes)
        {
            foreach (var item in result)
            {
                ct.ThrowIfCancellationRequested();
                item.SizeBytes = item.Kind switch
                {
                    LeftoverKind.Folder => PathUtil.DirectorySize(item.Path, ct),
                    LeftoverKind.File or LeftoverKind.Shortcut when File.Exists(item.Path) => new FileInfo(item.Path).Length,
                    _ => null,
                };
            }
        }
        return result.OrderByDescending(i => i.Confidence).ThenBy(i => i.Kind).ThenBy(i => i.Path).ToList();
    }

    private static void AddKey(HashSet<string> keys, string key)
    {
        if (!NameMatcher.IsGeneric(key)) keys.Add(key);
    }

    private static void ScanFolderRoot(string root, HashSet<string> nameKeys, string publisherKey,
        List<string> otherFolders, HashSet<string> otherKeys, Action<LeftoverItem> add)
    {
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root).ToList();
        }
        catch
        {
            return;
        }

        var isProfileRoot = root.Equals(KnownPaths.UserProfile, StringComparison.OrdinalIgnoreCase);
        // Documents may hold the user's own files (recordings, saves) — never pre-select those.
        var isUserDataRoot = root.Equals(KnownPaths.Documents, StringComparison.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (isProfileRoot && !name.StartsWith('.')) continue;
            if (IsOwnedByOtherProgram(dir, otherFolders)) continue;

            var score = NameMatcher.Score(name, nameKeys);
            if (score > 0)
            {
                var ambiguous = otherKeys.Contains(NameMatcher.Normalize(name)) && !nameKeys.Contains(NameMatcher.Normalize(name));
                add(new LeftoverItem
                {
                    Kind = LeftoverKind.Folder, Path = dir,
                    Confidence = ambiguous || isUserDataRoot ? Confidence.Low : score == 2 ? Confidence.High : Confidence.Medium,
                    Reason = isUserDataRoot ? "Folder in Documents named after the program — may contain your files"
                        : score == 2 ? "Folder named after the program" : "Folder with a similar name",
                });
                continue;
            }

            if (publisherKey.Length > 0 && !isUserDataRoot && IsPublisherMatch(name, publisherKey))
            {
                ScanPublisherFolder(dir, nameKeys, otherFolders, otherKeys, add);
            }
        }

        // Start Menu roots: loose shortcuts named after the program.
        if (root.Equals(KnownPaths.StartMenuPrograms, StringComparison.OrdinalIgnoreCase) ||
            root.Equals(KnownPaths.CommonStartMenuPrograms, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk"))
                {
                    if (NameMatcher.Score(Path.GetFileNameWithoutExtension(lnk), nameKeys) == 2)
                    {
                        add(new LeftoverItem
                        {
                            Kind = LeftoverKind.Shortcut, Path = lnk, Confidence = Confidence.High,
                            Reason = "Start Menu shortcut named after the program",
                        });
                    }
                }
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private static void ScanPublisherFolder(string publisherDir, HashSet<string> nameKeys, List<string> otherFolders,
        HashSet<string> otherKeys, Action<LeftoverItem> add)
    {
        List<string> children;
        try
        {
            children = Directory.EnumerateDirectories(publisherDir).ToList();
        }
        catch
        {
            return;
        }

        var matched = 0;
        foreach (var child in children)
        {
            if (IsOwnedByOtherProgram(child, otherFolders)) continue;
            var score = NameMatcher.Score(Path.GetFileName(child), nameKeys);
            if (score == 0) continue;
            matched++;
            var ambiguous = otherKeys.Contains(NameMatcher.Normalize(Path.GetFileName(child)));
            add(new LeftoverItem
            {
                Kind = LeftoverKind.Folder, Path = child,
                Confidence = ambiguous ? Confidence.Low : score == 2 ? Confidence.High : Confidence.Medium,
                Reason = "Program folder inside the publisher's folder",
            });
        }

        // The publisher folder itself would be left empty → offer it too (not pre-selected by default).
        var hasFiles = false;
        try { hasFiles = Directory.EnumerateFiles(publisherDir).Any(); } catch { /* ignore */ }
        if ((matched > 0 && matched == children.Count && !hasFiles) || (children.Count == 0 && !hasFiles))
        {
            if (!IsOwnedByOtherProgram(publisherDir, otherFolders))
            {
                add(new LeftoverItem
                {
                    Kind = LeftoverKind.Folder, Path = publisherDir, Confidence = Confidence.Medium,
                    Reason = children.Count == 0 ? "Empty publisher folder" : "Publisher folder with nothing else in it",
                });
            }
        }
    }

    private static bool IsPublisherMatch(string name, string publisherKey)
    {
        var n = NameMatcher.Normalize(name);
        return n == publisherKey || NameMatcher.NormalizePublisher(name) == publisherKey;
    }

    private static bool IsOwnedByOtherProgram(string dir, List<string> otherFolders) =>
        otherFolders.Any(f => PathUtil.IsUnderOrEqual(f, dir) || PathUtil.IsUnderOrEqual(dir, f));

    private static void ScanRegistryRoot(RegistryHive hive, RegistryView view, string subKey, HashSet<string> nameKeys,
        string publisherKey, HashSet<string> otherKeys, Action<LeftoverItem> add)
    {
        RegistryKey? root;
        try
        {
            root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(subKey);
        }
        catch
        {
            return;
        }
        if (root is null) return;

        using (root)
        {
            string[] names;
            try { names = root.GetSubKeyNames(); } catch { return; }
            var viewName = RegistryPath.ViewName(view);
            // HKCU has no WOW64 split; skip duplicate 32-bit scan of HKCU.
            foreach (var name in names)
            {
                if (name.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) continue;
                var fullPath = RegistryPath.Combine(hive, $@"{subKey}\{name}");
                var score = NameMatcher.Score(name, nameKeys);
                if (score > 0)
                {
                    var ambiguous = otherKeys.Contains(NameMatcher.Normalize(name)) && !nameKeys.Contains(NameMatcher.Normalize(name));
                    add(new LeftoverItem
                    {
                        Kind = LeftoverKind.RegistryKey, Path = fullPath, RegistryView = viewName,
                        Confidence = ambiguous ? Confidence.Low : score == 2 ? Confidence.High : Confidence.Medium,
                        Reason = score == 2 ? "Registry key named after the program" : "Registry key with a similar name",
                    });
                    continue;
                }

                if (publisherKey.Length == 0 || !IsPublisherMatch(name, publisherKey)) continue;
                try
                {
                    using var pub = root.OpenSubKey(name);
                    if (pub is null) continue;
                    var children = pub.GetSubKeyNames();
                    var matched = 0;
                    foreach (var child in children)
                    {
                        var s = NameMatcher.Score(child, nameKeys);
                        if (s == 0) continue;
                        matched++;
                        add(new LeftoverItem
                        {
                            Kind = LeftoverKind.RegistryKey, Path = $@"{fullPath}\{child}", RegistryView = viewName,
                            Confidence = s == 2 ? Confidence.High : Confidence.Medium,
                            Reason = "Program key inside the publisher's key",
                        });
                    }
                    if ((matched > 0 && matched == children.Length && pub.ValueCount == 0) ||
                        (children.Length == 0 && pub.ValueCount == 0))
                    {
                        add(new LeftoverItem
                        {
                            Kind = LeftoverKind.RegistryKey, Path = fullPath, RegistryView = viewName,
                            Confidence = Confidence.Medium,
                            Reason = children.Length == 0 ? "Empty publisher key" : "Publisher key with nothing else in it",
                        });
                    }
                }
                catch
                {
                    // Ignore unreadable publisher key.
                }
            }
        }
    }

    private void ScanSystemItems(ScanTarget target, HashSet<string> nameKeys, List<string> otherFolders, Action<LeftoverItem> add)
    {
        var folder = target.InstallFolder;
        if (folder is not null && !otherFolders.Any(f => PathUtil.IsUnderOrEqual(f, folder)))
        {
            foreach (var s in _footprint.ServicesUnder(folder))
            {
                add(new LeftoverItem
                {
                    Kind = LeftoverKind.Service, Path = s.Name, Confidence = Confidence.High,
                    Reason = "Service runs from the program folder", Detail = $"{s.DisplayName} — {s.BinaryPath}",
                });
            }
            foreach (var t in _footprint.TasksUnder(folder))
            {
                add(new LeftoverItem
                {
                    Kind = LeftoverKind.ScheduledTask, Path = t.Path, Confidence = Confidence.High,
                    Reason = "Scheduled task runs a program file", Detail = t.Command,
                });
            }
            foreach (var e in _footprint.StartupUnder(folder)) add(ToItem(e, Confidence.High, "Starts a program file with Windows"));
            foreach (var r in _footprint.FirewallUnder(folder))
            {
                add(new LeftoverItem
                {
                    Kind = LeftoverKind.FirewallRule, Path = r.Name, Confidence = Confidence.Medium,
                    Reason = "Firewall rule for a program file", Detail = r.ApplicationPath,
                });
            }
            foreach (var sc in _footprint.ShortcutsUnder(folder))
            {
                add(new LeftoverItem
                {
                    Kind = LeftoverKind.Shortcut, Path = sc.Path, Confidence = Confidence.High,
                    Reason = "Shortcut points into the program folder", Detail = sc.Target,
                });
            }
        }

        // Shortcuts whose target no longer exists and whose name matches the program.
        foreach (var sc in _footprint.Shortcuts)
        {
            if (sc.Target is null || File.Exists(sc.Target) || Directory.Exists(sc.Target)) continue;
            if (NameMatcher.Score(Path.GetFileNameWithoutExtension(sc.Path), nameKeys) == 0) continue;
            add(new LeftoverItem
            {
                Kind = LeftoverKind.Shortcut, Path = sc.Path, Confidence = Confidence.High,
                Reason = "Broken shortcut named after the program", Detail = sc.Target,
            });
        }

        // Scheduled tasks named after the program (their files may already be gone).
        foreach (var t in _footprint.Tasks)
        {
            if (NameMatcher.Score(t.Name, nameKeys) == 0) continue;
            var exeMissing = t.ExecutablePath is not null && !File.Exists(t.ExecutablePath);
            add(new LeftoverItem
            {
                Kind = LeftoverKind.ScheduledTask, Path = t.Path,
                Confidence = exeMissing ? Confidence.High : Confidence.Medium,
                Reason = exeMissing ? "Task named after the program; its file is gone" : "Task named after the program",
                Detail = t.Command,
            });
        }

        // Startup entries pointing at files that no longer exist, named after the program.
        foreach (var e in _footprint.Startup)
        {
            if (e.TargetPath is null || File.Exists(e.TargetPath)) continue;
            if (NameMatcher.Score(e.Name, nameKeys) == 0) continue;
            add(ToItem(e, Confidence.High, "Startup entry for a missing file"));
        }
    }

    public static LeftoverItem ToItem(StartupEntry e, Confidence confidence, string reason) =>
        e.RegistryKey is not null
            ? new LeftoverItem
            {
                Kind = LeftoverKind.StartupEntry, Path = e.RegistryKey, ValueName = e.Name, RegistryView = e.RegistryViewName,
                Confidence = confidence, Reason = reason, Detail = e.Command,
            }
            : new LeftoverItem
            {
                Kind = LeftoverKind.StartupEntry, Path = e.FilePath!, Confidence = confidence, Reason = reason, Detail = e.Command,
            };

    /// <summary>
    /// Drops items already covered by a parent folder/key in the list — but only when the parent is at least as
    /// confident, so a High-confidence install folder is never hidden inside a Medium-confidence publisher folder.
    /// </summary>
    public static List<LeftoverItem> Collapse(List<LeftoverItem> items)
    {
        var folders = items.Where(i => i.Kind == LeftoverKind.Folder).ToList();
        var keys = items.Where(i => i.Kind == LeftoverKind.RegistryKey).ToList();
        return items.Where(i =>
        {
            switch (i.Kind)
            {
                case LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut:
                case LeftoverKind.StartupEntry when i.ValueName is null:
                    return !folders.Any(f => f.Confidence >= i.Confidence && PathUtil.IsStrictlyUnder(i.Path, f.Path));
                case LeftoverKind.RegistryKey:
                    return !keys.Any(k => k.Confidence >= i.Confidence && k.RegistryView == i.RegistryView &&
                                          i.Path.StartsWith(k.Path + "\\", StringComparison.OrdinalIgnoreCase));
                default:
                    return true;
            }
        }).ToList();
    }

    /// <summary>
    /// When every entry in a Start Menu sub-folder is one of the program's shortcuts, offer the whole folder.
    /// </summary>
    private static void AddStartMenuFolders(Dictionary<string, LeftoverItem> items, Action<LeftoverItem> add)
    {
        var shortcuts = items.Values.Where(i => i.Kind == LeftoverKind.Shortcut && i.Confidence == Confidence.High).ToList();
        foreach (var group in shortcuts.GroupBy(s => Path.GetDirectoryName(s.Path)!, StringComparer.OrdinalIgnoreCase))
        {
            var folder = group.Key;
            var inStartMenu = PathUtil.IsStrictlyUnder(folder, KnownPaths.StartMenuPrograms) ||
                              PathUtil.IsStrictlyUnder(folder, KnownPaths.CommonStartMenuPrograms);
            if (!inStartMenu) continue;
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(folder)
                    .Where(e => !Path.GetFileName(e).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).ToList();
                var covered = group.Select(g => g.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (entries.Count > 0 && entries.All(covered.Contains))
                {
                    add(new LeftoverItem
                    {
                        Kind = LeftoverKind.Folder, Path = folder, Confidence = Confidence.High,
                        Reason = "Start Menu folder containing only the program's shortcuts",
                    });
                }
            }
            catch
            {
                // Ignore unreadable folder.
            }
        }
    }
}
