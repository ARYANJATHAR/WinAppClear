using System.Diagnostics;
using WinAppClear.Core.Browsers;
using WinAppClear.Core.Health;
using WinAppClear.Core.Inventory;
using WinAppClear.Core.Leftovers;
using WinAppClear.Core.Monitor;
using WinAppClear.Core.Store;
using WinAppClear.Core.Uninstall;
using WinAppClear.Core.Updates;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

// WinAppClear command-line tool. Every command here is read-only: it lists and scans, never removes.
Console.OutputEncoding = System.Text.Encoding.UTF8;
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var rest = args.Skip(1).ToArray();
var sw = Stopwatch.StartNew();

switch (command)
{
    case "list":
    {
        var programs = ProgramInventory.GetInstalledPrograms();
        UsageTracker.Apply(programs, UsageTracker.ReadLastRunTimes());
        foreach (var p in programs)
        {
            var folder = ProgramInventory.GuessInstallFolder(p);
            Console.WriteLine($"{p.DisplayName,-50} {p.DisplayVersion,-16} {p.ArchitectureLabel,-7} {UninstallCommand.Detect(p),-13} " +
                              $"{PathUtil.FormatSize(p.EstimatedSizeBytes),9}  used:{p.LastUsed:yyyy-MM-dd}  {folder}");
        }
        Console.WriteLine($"\n{programs.Count} programs");
        break;
    }

    case "commands":
    {
        foreach (var p in ProgramInventory.GetInstalledPrograms())
        {
            Console.WriteLine(p.DisplayName);
            Console.WriteLine($"   normal: {UninstallCommand.Normal(p)}");
            Console.WriteLine($"   quiet : {UninstallCommand.Quiet(p)?.ToString() ?? "(none)"}");
        }
        break;
    }

    case "scan":
    {
        if (rest.Length == 0)
        {
            Console.WriteLine("usage: scan <part of program name>");
            return 1;
        }
        var programs = ProgramInventory.GetInstalledPrograms();
        var target = programs.FirstOrDefault(p => p.DisplayName.Contains(rest[0], StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            Console.WriteLine("No matching program.");
            return 1;
        }
        Console.WriteLine($"Scanning footprint of {target.DisplayName} (install folder: {ProgramInventory.GuessInstallFolder(target)})");
        var scanner = new LeftoverScanner(null, programs);
        foreach (var item in scanner.Scan(ScanTarget.FromProgram(target)))
            Console.WriteLine($"  [{item.Confidence,-6}] {item.KindLabel,-15} {item.DisplayPath}  {PathUtil.FormatSize(item.SizeBytes)}  — {item.Reason}");
        break;
    }

    case "folder":
    {
        var scanner = new LeftoverScanner(null, ProgramInventory.GetInstalledPrograms()
            .Where(p => !PathUtil.IsUnderOrEqual(ProgramInventory.GuessInstallFolder(p), rest[0])).ToList());
        foreach (var item in scanner.Scan(ScanTarget.FromFolder(rest[0])))
            Console.WriteLine($"  [{item.Confidence,-6}] {item.KindLabel,-15} {item.DisplayPath}  — {item.Reason}");
        break;
    }

    case "health":
    {
        var context = new HealthContext();
        var results = await HealthScanner.ScanAllAsync(context);
        foreach (var r in results)
        {
            Console.WriteLine($"\n== {r.Check.Title}: {r.Issues.Count} issue(s){(r.Error is null ? "" : $"  ERROR: {r.Error}")}");
            foreach (var i in r.Issues.Take(rest.Contains("--all") ? int.MaxValue : 8))
                Console.WriteLine($"   {(i.Recommended ? "*" : " ")} {i.Title} | {i.Detail} {PathUtil.FormatSize(i.SizeBytes)}");
        }
        break;
    }

    case "store":
        foreach (var a in StoreAppService.GetApps())
            Console.WriteLine($"{a.DisplayName,-45} {a.Version,-16} {(a.IsRemovable ? "" : "[protected]")} {a.Name}");
        break;

    case "ext":
        foreach (var e in BrowserExtensionService.GetExtensions())
            Console.WriteLine($"{e.BrowserName,-8} {e.Profile,-12} {e.Name,-45} {e.Version,-12} {(e.Enabled ? "on" : "off")} {(e.HasBroadAccess ? "[broad access]" : "")}");
        break;

    case "updates":
    {
        var (updates, error) = await WingetClient.GetUpdatesAsync();
        if (error is not null) Console.WriteLine($"winget: {error}");
        foreach (var u in updates) Console.WriteLine($"{u.Name,-45} {u.Id,-40} {u.InstalledVersion} -> {u.AvailableVersion}");
        break;
    }

    case "startup":
        foreach (var e in StartupManager.GetEntries())
            Console.WriteLine($"{(e.Enabled ? "on " : "off")} {e.LocationLabel,-32} {e.Name,-30} {e.TargetPath}");
        break;

    case "together":
        foreach (var g in ProgramLookup.InstalledTogether(ProgramInventory.GetInstalledPrograms()))
            Console.WriteLine($"{g[0].RegistryTimestamp:g}: {string.Join(" + ", g.Select(p => p.DisplayName))}");
        break;

    case "snapshot":
    {
        var snap = SystemSnapshot.Take(new Progress<string>(Console.WriteLine));
        Console.WriteLine($"{snap.Files.Count} files, {snap.Directories.Count} folders, {snap.RegistryKeys.Count} keys, {snap.RegistryValues.Count} values");
        break;
    }

    case "protected":
        foreach (var p in rest) Console.WriteLine($"{p}: {SafetyGuard_(p)}");
        break;

    default:
        Console.WriteLine("""
            winappclear-cli — read-only diagnostics for WinAppClear
              list                 installed programs
              commands             uninstall / quiet uninstall commands
              scan <name>          what WinAppClear would find for a program (dry run)
              folder <path>        force-uninstall scan of a folder (dry run)
              health [--all]       Software Health scan (no fixes)
              store | ext | updates | startup | together
              snapshot             time an install-monitor snapshot
              protected <path...>  ask the safety guard about paths/keys
            """);
        break;
}

Console.Error.WriteLine($"({sw.Elapsed.TotalSeconds:0.0}s)");
return 0;

static string SafetyGuard_(string p) =>
    (p.StartsWith("HK", StringComparison.OrdinalIgnoreCase)
        ? WinAppClear.Core.Safety.SafetyGuard.ProtectedRegistryKeyReason(p)
        : WinAppClear.Core.Safety.SafetyGuard.ProtectedPathReason(p)) ?? "allowed";
