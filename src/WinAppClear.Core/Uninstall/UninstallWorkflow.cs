using WinAppClear.Core.Cleanup;
using WinAppClear.Core.History;
using WinAppClear.Core.Inventory;
using WinAppClear.Core.Leftovers;
using WinAppClear.Core.Models;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Uninstall;

public sealed class UninstallOptions
{
    public bool Quiet { get; init; }
    public bool CreateRestorePoint { get; init; } = true;
    /// <summary>Remove High-confidence leftovers automatically (no review step).</summary>
    public bool AutoRemoveHighConfidence { get; init; }
}

/// <summary>
/// The full uninstall pipeline used by the UI wizard, batch uninstall and Software Health:
/// restore point → program's own uninstaller → leftover scan → backup + removal → history entry.
/// </summary>
public static class UninstallWorkflow
{
    public static Task<RestorePointService.Result> CreateRestorePointAsync(IEnumerable<InstalledProgram> programs)
    {
        var names = string.Join(", ", programs.Select(p => p.DisplayName));
        return RestorePointService.CreateAsync($"WinAppClear: before uninstalling {names}");
    }

    public static Task<List<LeftoverItem>> ScanLeftoversAsync(InstalledProgram program, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var remaining = ProgramInventory.GetInstalledPrograms();
            var scanner = new LeftoverScanner(null, remaining);
            return scanner.Scan(ScanTarget.FromProgram(program), ct);
        }, ct);

    public static Task<List<LeftoverItem>> ScanFolderAsync(string folder, string? name, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var programs = ProgramInventory.GetInstalledPrograms()
                .Where(p => !PathUtil.IsUnderOrEqual(ProgramInventory.GuessInstallFolder(p), folder)).ToList();
            var scanner = new LeftoverScanner(null, programs);
            return scanner.Scan(ScanTarget.FromFolder(folder, name), ct);
        }, ct);

    public static Task<CleanupReport> RemoveAsync(string title, IReadOnlyList<LeftoverItem> items, IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => CleanupExecutor.Execute(title, items, progress, ct), ct);

    public static HistoryEntry Record(InstalledProgram program, RemovalMethod method, UninstallResult? result,
        IReadOnlyList<LeftoverItem> found, CleanupReport? report)
    {
        var removedKeys = report?.Results.Where(r => r.Status is CleanupStatus.Removed or CleanupStatus.ScheduledForReboot)
            .Select(r => r.Item.Key).ToHashSet() ?? [];
        var entry = new HistoryEntry
        {
            Name = program.DisplayName,
            Publisher = program.Publisher,
            Version = program.DisplayVersion,
            InstallFolder = ProgramInventory.GuessInstallFolder(program),
            Method = method,
            Result = result?.Message ?? "Leftovers removed",
            ExitCode = result?.ExitCode,
            LeftoversFound = found.Count,
            LeftoversRemoved = removedKeys.Count,
            BytesFreed = found.Where(f => removedKeys.Contains(f.Key)).Sum(f => f.SizeBytes ?? 0),
            BackupId = report?.BackupId,
            RemainingLeftovers = found.Where(f => !removedKeys.Contains(f.Key) && f.Confidence >= Confidence.Medium).ToList(),
        };
        HistoryStore.Add(entry);
        return entry;
    }

    /// <summary>Non-interactive pipeline (used by batch mode and Software Health).</summary>
    public static async Task<HistoryEntry> RunUnattendedAsync(InstalledProgram program, UninstallOptions options,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (options.CreateRestorePoint)
        {
            progress?.Report("Creating a restore point…");
            await CreateRestorePointAsync([program]);
        }

        progress?.Report($"Uninstalling {program.DisplayName}…");
        var result = await UninstallRunner.RunAsync(program, options.Quiet, progress, ct);
        if (result.Outcome is UninstallOutcome.Cancelled or UninstallOutcome.Failed)
            return Record(program, options.Quiet ? RemovalMethod.Quiet : RemovalMethod.Standard, result, [], null);

        progress?.Report($"Scanning for leftovers of {program.DisplayName}…");
        var found = await ScanLeftoversAsync(program, ct);
        CleanupReport? report = null;
        if (options.AutoRemoveHighConfidence && result.Outcome == UninstallOutcome.Removed)
        {
            var high = found.Where(f => f.Confidence == Confidence.High).ToList();
            if (high.Count > 0) report = await RemoveAsync($"Leftovers of {program.DisplayName}", high, progress, ct);
        }
        return Record(program, options.Quiet ? RemovalMethod.Quiet : RemovalMethod.Standard, result, found, report);
    }
}
