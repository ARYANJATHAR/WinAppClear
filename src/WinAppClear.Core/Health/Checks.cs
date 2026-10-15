using System.Text.Json.Nodes;
using WinAppClear.Core.Browsers;
using WinAppClear.Core.Cleanup;
using WinAppClear.Core.History;
using WinAppClear.Core.Inventory;
using WinAppClear.Core.Leftovers;
using WinAppClear.Core.Models;
using WinAppClear.Core.Safety;
using WinAppClear.Core.Uninstall;
using WinAppClear.Core.Updates;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;
using Microsoft.Win32;

namespace WinAppClear.Core.Health;

/// <summary>1. Leftovers from earlier uninstalls that are still on disk / in the registry.</summary>
public sealed class LeftoversCheck : IHealthCheck
{
    public HealthCategory Category => HealthCategory.Leftovers;
    public string Title => "Uninstallation Leftovers";
    public string Description => "Files and registry entries left behind by programs you already uninstalled.";
    public string FixLabel => "Remove";

    public Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct) => Task.Run(() =>
    {
        var issues = new List<HealthIssue>();
        var installedNames = context.Programs.Select(p => NameMatcher.Normalize(p.DisplayName)).ToHashSet();
        var scanner = new LeftoverScanner(LeftoverScanOptions.Default, context.Programs, context.Footprint);
        var seen = new HashSet<string>();

        foreach (var entry in HistoryStore.All().Where(e => e.Method is not RemovalMethod.AndroidApp and not RemovalMethod.BrowserExtension
                                                             and not RemovalMethod.StoreApp))
        {
            ct.ThrowIfCancellationRequested();
            if (installedNames.Contains(NameMatcher.Normalize(entry.Name))) continue; // reinstalled since

            foreach (var item in entry.RemainingLeftovers.Where(Exists))
            {
                if (seen.Add(item.Key))
                    issues.Add(Issue(entry.Name, item, item.Confidence == Confidence.High));
            }

            var target = new ScanTarget(entry.Name, entry.Publisher, entry.InstallFolder, null, RegistryView.Registry64, null);
            foreach (var item in scanner.Scan(target, ct).Where(i => i.Confidence == Confidence.High))
            {
                if (seen.Add(item.Key)) issues.Add(Issue(entry.Name, item, true));
            }
        }
        return issues;
    }, ct);

    private static HealthIssue Issue(string program, LeftoverItem item, bool recommended) => new()
    {
        Category = HealthCategory.Leftovers, Title = $"{program}: {item.KindLabel}", Detail = item.DisplayPath,
        SizeBytes = item.SizeBytes, Recommended = recommended, Payload = item,
        Severity = HealthSeverity.Info,
    };

    internal static bool Exists(LeftoverItem i) => i.Kind switch
    {
        LeftoverKind.Folder => Directory.Exists(i.Path),
        LeftoverKind.File or LeftoverKind.Shortcut => File.Exists(i.Path),
        LeftoverKind.StartupEntry when i.ValueName is null => File.Exists(i.Path),
        LeftoverKind.RegistryKey => RegistryPath.KeyExists(i.Path, RegistryPath.ParseView(i.RegistryView)),
        LeftoverKind.RegistryValue or LeftoverKind.StartupEntry => RegistryPath.ValueExists(i.Path, i.ValueName!, RegistryPath.ParseView(i.RegistryView)),
        LeftoverKind.Service => ServiceManager.Exists(i.Path),
        LeftoverKind.ScheduledTask => TaskSchedulerService.Exists(i.Path),
        _ => false,
    };

    public Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var items = issues.Select(i => i.Payload).OfType<LeftoverItem>().ToList();
            var report = CleanupExecutor.Execute("Software Health: leftovers", items, progress, ct);
            return new HealthFixResult(report.Removed + report.Scheduled, report.Failed,
                report.Results.Where(r => r.Message is not null && r.Status != CleanupStatus.Removed)
                    .Select(r => $"{r.Item.DisplayPath}: {r.Message}").ToList(), report.BackupId);
        }, ct);
}

/// <summary>2. Programs with newer versions available through winget.</summary>
public sealed class OutdatedSoftwareCheck : IHealthCheck
{
    public HealthCategory Category => HealthCategory.Outdated;
    public string Title => "Outdated Software";
    public string Description => "Update programs to get security fixes (uses the Windows Package Manager).";
    public string FixLabel => "Update";

    public async Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct)
    {
        var (updates, error) = await WingetClient.GetUpdatesAsync(ct);
        if (error is not null && updates.Count == 0)
        {
            return
            [
                new HealthIssue
                {
                    Category = Category, Title = "Update check unavailable", Detail = error, Severity = HealthSeverity.Info,
                },
            ];
        }
        return updates.Select(u => new HealthIssue
        {
            Category = Category, Title = u.Name, Detail = $"{u.InstalledVersion} → {u.AvailableVersion}  ({u.Id})",
            Recommended = true, Severity = HealthSeverity.Warning, Payload = u,
        }).ToList();
    }

    public async Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct)
    {
        int ok = 0, failed = 0;
        var messages = new List<string>();
        foreach (var u in issues.Select(i => i.Payload).OfType<AvailableUpdate>())
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Updating {u.Name}…");
            var (success, output) = await WingetClient.UpgradeAsync(u.Id, ct);
            if (success) ok++;
            else
            {
                failed++;
                messages.Add($"{u.Name}: {output}");
            }
        }
        return new HealthFixResult(ok, failed, messages);
    }
}

/// <summary>3. Background permissions programs gave themselves (startup, services, tasks, menus, firewall).</summary>
public sealed class PermissionsCheck : IHealthCheck
{
    public HealthCategory Category => HealthCategory.Permissions;
    public string Title => "Software Permissions";
    public string Description => "Things programs set up to run or act in the background. Switch off what you don't need.";
    public string FixLabel => "Switch off";

    public Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct) => Task.Run(() =>
    {
        var issues = new List<HealthIssue>();
        foreach (var p in context.Programs)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in FootprintBuilder.ActiveItems(p, context.Footprint))
            {
                issues.Add(new HealthIssue
                {
                    Category = Category, Title = $"{p.DisplayName}: {item.KindLabel}", Detail = item.Display,
                    Payload = item, Recommended = false,
                    Severity = item.Kind == ToggleKind.FirewallRule ? HealthSeverity.Warning : HealthSeverity.Info,
                });
            }
        }
        return issues;
    }, ct);

    public Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => ToggleFix.DisableAll(issues, progress), ct);
}

internal static class ToggleFix
{
    public static HealthFixResult DisableAll(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress)
    {
        int ok = 0, failed = 0;
        var messages = new List<string>();
        foreach (var item in issues.Select(i => i.Payload).OfType<ToggleItem>())
        {
            progress?.Report($"Switching off {item.Display}…");
            if (ToggleService.Disable(item)) ok++;
            else
            {
                failed++;
                messages.Add($"Could not switch off {item.Display}");
            }
        }
        return new HealthFixResult(ok, failed, messages);
    }
}

/// <summary>4. Apps and websites allowed to show pop-up notifications.</summary>
public sealed class NotificationsCheck : IHealthCheck
{
    public HealthCategory Category => HealthCategory.Notifications;
    public string Title => "Disturbing Notifications";
    public string Description => "Apps and websites that are allowed to pop up notifications.";
    public string FixLabel => "Block";

    public Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct) => Task.Run(() =>
    {
        var issues = new List<HealthIssue>();
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(ToggleService.NotificationSettingsKey);
            if (settings is not null)
            {
                foreach (var appId in settings.GetSubKeyNames())
                {
                    if (IsSystemNotifier(appId)) continue;
                    using var key = settings.OpenSubKey(appId);
                    if (key?.GetValue("Enabled") is int enabled && enabled == 0) continue;
                    issues.Add(new HealthIssue
                    {
                        Category = Category, Title = FriendlyAppId(appId), Detail = "Windows notifications allowed",
                        Payload = new ToggleItem
                        {
                            Kind = ToggleKind.AppNotification, Owner = FriendlyAppId(appId), Key = appId,
                            Display = $"{FriendlyAppId(appId)} notifications",
                        },
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Notification settings read: {ex.Message}");
        }

        foreach (var profile in BrowserExtensionService.GetProfiles().Where(p => p.Browser != BrowserKind.Firefox))
        {
            var prefs = Path.Combine(profile.ProfilePath, "Preferences");
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(prefs));
                var exceptions = json?["profile"]?["content_settings"]?["exceptions"]?["notifications"]?.AsObject();
                if (exceptions is null) continue;
                foreach (var (pattern, node) in exceptions)
                {
                    if (node?["setting"]?.GetValue<int>() != 1) continue;
                    var site = pattern.Split(',')[0];
                    issues.Add(new HealthIssue
                    {
                        Category = Category, Title = site, Detail = $"Can send notifications in {profile.Browser} ({profile.ProfileName})",
                        Recommended = true, Severity = HealthSeverity.Warning,
                        Payload = new ToggleItem
                        {
                            Kind = ToggleKind.SiteNotification, Owner = $"{profile.Browser} ({profile.ProfileName})", Key = pattern,
                            Extra = prefs, Display = $"{site} notifications in {profile.Browser}",
                        },
                    });
                }
            }
            catch
            {
                // Unreadable profile — skip.
            }
        }
        return issues;
    }, ct);

    private static bool IsSystemNotifier(string appId) =>
        appId.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) ||
        appId.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) ||
        appId.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase) ||
        appId.StartsWith("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase) ||
        appId.Contains("Windows.SystemToast", StringComparison.OrdinalIgnoreCase) ||
        appId.Equals("Windows.SystemToast.SecurityAndMaintenance", StringComparison.OrdinalIgnoreCase);

    public static string FriendlyAppId(string appId)
    {
        var id = appId;
        var bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];
        var underscore = id.IndexOf('_');
        if (underscore > 0 && id.Length - underscore == 14) id = id[..underscore]; // package family suffix
        if (id.Contains('\\') || id.Contains('/')) id = Path.GetFileNameWithoutExtension(id.Replace('/', '\\'));
        return id;
    }

    public Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var browsersOpen = issues.Select(i => i.Payload).OfType<ToggleItem>()
                .Where(t => t.Kind == ToggleKind.SiteNotification)
                .Select(t => t.Owner.Split(' ')[0])
                .Distinct()
                .Where(b => Enum.TryParse<BrowserKind>(b, out var k) && BrowserExtensionService.IsBrowserRunning(k))
                .ToList();
            if (browsersOpen.Count > 0)
            {
                return new HealthFixResult(0, issues.Count,
                    [$"Close {string.Join(" and ", browsersOpen)} first — browsers overwrite their settings while open."]);
            }
            return ToggleFix.DisableAll(issues, progress);
        }, ct);
}

/// <summary>5. Programs whose uninstaller is missing or broken.</summary>
public sealed class UninstallIssuesCheck : IHealthCheck
{
    public HealthCategory Category => HealthCategory.UninstallIssues;
    public string Title => "Programs with Uninstall Issues";
    public string Description => "Programs that can't be uninstalled normally because their uninstaller is missing.";
    public string FixLabel => "Clean up";

    public Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct) => Task.Run(() =>
    {
        var issues = new List<HealthIssue>();
        foreach (var p in context.Programs)
        {
            var cmd = UninstallCommand.Normal(p);
            if (cmd is null || UninstallCommand.IsMsiExec(cmd.FileName)) continue;
            if (UninstallCommand.ResolveExecutable(cmd.FileName) is not null) continue;
            var folderGone = p.InstallLocation is null || !Directory.Exists(p.InstallLocation);
            issues.Add(new HealthIssue
            {
                Category = Category, Title = p.DisplayName,
                Detail = folderGone ? "Uninstaller and program folder are gone — only the entry remains"
                    : $"Uninstaller missing: {cmd.FileName}",
                Recommended = folderGone, Severity = HealthSeverity.Warning, Payload = p,
            });
        }
        return issues;
    }, ct);

    public Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            int ok = 0, failed = 0;
            var messages = new List<string>();
            var programs = ProgramInventory.GetInstalledPrograms();
            var scanner = new LeftoverScanner(null, programs);
            foreach (var p in issues.Select(i => i.Payload).OfType<InstalledProgram>())
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Cleaning up {p.DisplayName}…");
                var items = scanner.Scan(ScanTarget.FromProgram(p), ct).Where(i => i.Confidence == Confidence.High).ToList();
                var report = CleanupExecutor.Execute($"Broken entry: {p.DisplayName}", items, progress, ct);
                HistoryStore.Add(new HistoryEntry
                {
                    Name = p.DisplayName, Publisher = p.Publisher, Version = p.DisplayVersion, Method = RemovalMethod.BrokenEntry,
                    InstallFolder = ProgramInventory.GuessInstallFolder(p), Result = "Broken entry cleaned up",
                    LeftoversFound = items.Count, LeftoversRemoved = report.Removed, BackupId = report.BackupId,
                });
                if (!ProgramInventory.StillInstalled(p)) ok++;
                else
                {
                    failed++;
                    messages.Add($"{p.DisplayName}: the entry could not be removed");
                }
            }
            return new HealthFixResult(ok, failed, messages);
        }, ct);
}

/// <summary>6. Old installer files sitting in Downloads.</summary>
public sealed class InstallationFilesCheck : IHealthCheck
{
    private static readonly string[] Extensions = [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle"];

    public HealthCategory Category => HealthCategory.InstallationFiles;
    public string Title => "Installation Files";
    public string Description => "Setup files in your Downloads folder that are no longer needed.";
    public string FixLabel => "Move to Recycle Bin";

    public Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct) => Task.Run(() =>
    {
        var issues = new List<HealthIssue>();
        var downloads = KnownPaths.Downloads;
        if (!Directory.Exists(downloads)) return issues;
        var files = Directory.EnumerateFiles(downloads, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true,
        });
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(f);
            var age = DateTime.Now - info.LastWriteTime;
            if (age < TimeSpan.FromDays(1)) continue; // probably still being used
            issues.Add(new HealthIssue
            {
                Category = Category, Title = info.Name,
                Detail = $"{Path.GetDirectoryName(f)} — downloaded {info.LastWriteTime:d}",
                SizeBytes = info.Length, Recommended = age > TimeSpan.FromDays(30), Payload = f,
            });
        }
        return issues.OrderByDescending(i => i.SizeBytes).ToList();
    }, ct);

    public Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            int ok = 0, failed = 0;
            var messages = new List<string>();
            foreach (var file in issues.Select(i => i.Payload).OfType<string>())
            {
                if (RecycleBin.Send(file)) ok++;
                else
                {
                    failed++;
                    messages.Add($"Could not recycle {file}");
                }
            }
            return new HealthFixResult(ok, failed, messages);
        }, ct);
}

/// <summary>8. Caches, crash dumps and old temp files programs leave around while you use them.</summary>
public sealed class RedundantFilesCheck : IHealthCheck
{
    private static readonly string[] CacheFolderNames = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "ShaderCache", "GrShaderCache", "Crashpad\\reports", "CrashDumps", "logs"];
    private static readonly string[] SkipVendors = ["Google", "Microsoft", "Mozilla", "BraveSoftware", "Vivaldi", "Opera Software", "WinAppClear", "Packages", "Programs", "Temp", "NVIDIA", "NVIDIA Corporation"];

    public HealthCategory Category => HealthCategory.RedundantFiles;
    public string Title => "Software Redundant Files";
    public string Description => "Caches, logs, crash dumps and temp files programs create while you use them.";
    public string FixLabel => "Clean";

    public sealed record JunkGroup(string Owner, List<string> Paths, bool DeleteContentsOnly);

    public Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct) => Task.Run(() =>
    {
        var issues = new List<HealthIssue>();

        foreach (var root in new[] { KnownPaths.AppData, KnownPaths.LocalAppData })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var appDir in SafeDirs(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(appDir);
                if (SkipVendors.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                var found = new List<string>();
                foreach (var candidate in CandidateDirs(appDir))
                {
                    foreach (var cacheName in CacheFolderNames)
                    {
                        var p = Path.Combine(candidate, cacheName);
                        if (Directory.Exists(p) && !SafetyGuard.IsProtectedPath(p)) found.Add(p);
                    }
                }
                if (found.Count == 0) continue;
                var size = found.Sum(f => PathUtil.DirectorySize(f, ct));
                if (size < 1024 * 1024) continue;
                issues.Add(new HealthIssue
                {
                    Category = Category, Title = $"{name} — cache and logs", Detail = string.Join("; ", found.Take(3)),
                    SizeBytes = size, Recommended = true, Payload = new JunkGroup(name, found, DeleteContentsOnly: true),
                });
            }
        }

        var dumps = Path.Combine(KnownPaths.LocalAppData, "CrashDumps");
        if (Directory.Exists(dumps))
        {
            var files = Directory.EnumerateFiles(dumps).ToList();
            var size = files.Sum(f => new FileInfo(f).Length);
            if (files.Count > 0)
            {
                issues.Add(new HealthIssue
                {
                    Category = Category, Title = "Program crash dumps", Detail = $"{files.Count} file(s) in {dumps}", SizeBytes = size,
                    Recommended = true, Payload = new JunkGroup("Crash dumps", files, DeleteContentsOnly: false),
                });
            }
        }

        var temp = PathUtil.TrimEnd(KnownPaths.Temp);
        if (Directory.Exists(temp))
        {
            var cutoff = DateTime.Now.AddDays(-7);
            var old = new List<string>();
            long size = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(temp))
            {
                try
                {
                    var info = new FileInfo(entry);
                    var isDir = Directory.Exists(entry);
                    var time = isDir ? Directory.GetLastWriteTime(entry) : info.LastWriteTime;
                    if (time > cutoff) continue;
                    old.Add(entry);
                    size += isDir ? PathUtil.DirectorySize(entry, ct) : info.Length;
                }
                catch
                {
                    // Skip.
                }
            }
            if (old.Count > 0)
            {
                issues.Add(new HealthIssue
                {
                    Category = Category, Title = "Old temporary files", Detail = $"{old.Count} item(s) older than 7 days in {temp}",
                    SizeBytes = size, Recommended = true, Payload = new JunkGroup("Temp", old, DeleteContentsOnly: false),
                });
            }
        }
        return issues.OrderByDescending(i => i.SizeBytes).ToList();
    }, ct);

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); } catch { return []; }
    }

    private static IEnumerable<string> CandidateDirs(string appDir)
    {
        yield return appDir;
        foreach (var sub in SafeDirs(appDir)) yield return sub;
    }

    public Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            int ok = 0, failed = 0;
            foreach (var group in issues.Select(i => i.Payload).OfType<JunkGroup>())
            {
                progress?.Report($"Cleaning {group.Owner}…");
                foreach (var path in group.Paths)
                {
                    ct.ThrowIfCancellationRequested();
                    if (SafetyGuard.IsProtectedPath(path)) continue;
                    if (group.DeleteContentsOnly && Directory.Exists(path))
                    {
                        foreach (var child in Directory.EnumerateFileSystemEntries(path))
                        {
                            if (TryDelete(child)) ok++;
                            else failed++;
                        }
                    }
                    else if (TryDelete(path)) ok++;
                    else failed++;
                }
            }
            var messages = failed > 0 ? new List<string> { $"{failed} item(s) were in use and were skipped." } : [];
            return new HealthFixResult(ok, 0, messages);
        }, ct);

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>9. Programs not used for a long time that still run in the background.</summary>
public sealed class HibernationCheck : IHealthCheck
{
    public HealthCategory Category => HealthCategory.Hibernation;
    public string Title => "Software Hibernation";
    public string Description => "Put programs you're not using to sleep so they stop running in the background. Wake them any time.";
    public string FixLabel => "Hibernate";

    public static readonly TimeSpan UnusedFor = TimeSpan.FromDays(30);

    public Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct) => Task.Run(() =>
    {
        var issues = new List<HealthIssue>();
        foreach (var p in context.Programs)
        {
            ct.ThrowIfCancellationRequested();
            if (HibernationService.IsHibernated(p.Id)) continue;
            var items = FootprintBuilder.BackgroundItems(p, context.Footprint);
            if (items.Count == 0) continue;
            var unused = p.LastUsed is { } last && DateTime.Now - last > UnusedFor;
            issues.Add(new HealthIssue
            {
                Category = Category, Title = p.DisplayName,
                Detail = $"{items.Count} background item(s): {string.Join(", ", items.Select(i => i.KindLabel).Distinct())}" +
                         (p.LastUsed is { } lu ? $" — last used {lu:d}" : " — last use unknown"),
                Recommended = unused, Payload = p,
            });
        }
        return issues.OrderByDescending(i => i.Recommended).ToList();
    }, ct);

    public Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            int ok = 0, failed = 0;
            var messages = new List<string>();
            var footprint = new SystemFootprint();
            foreach (var p in issues.Select(i => i.Payload).OfType<InstalledProgram>())
            {
                progress?.Report($"Hibernating {p.DisplayName}…");
                var (success, message) = HibernationService.Hibernate(p, footprint, closeRunning: false);
                if (success) ok++;
                else
                {
                    failed++;
                    messages.Add(message);
                }
            }
            return new HealthFixResult(ok, failed, messages);
        }, ct);
}
