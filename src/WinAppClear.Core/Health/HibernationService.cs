using WinAppClear.Core.Inventory;
using WinAppClear.Core.Leftovers;
using WinAppClear.Core.Models;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Health;

public sealed class HibernationRecord
{
    public required string ProgramId { get; init; }
    public required string ProgramName { get; init; }
    public string? InstallFolder { get; init; }
    public DateTime Date { get; init; } = DateTime.Now;
    public List<ToggleItem> Items { get; init; } = [];
}

public sealed class HibernationData
{
    public List<HibernationRecord> Records { get; set; } = [];
}

/// <summary>
/// "Software Hibernation": puts an unused program to sleep by switching off everything that makes it run in
/// the background (startup entries, auto-start services, scheduled tasks). Waking it restores each item.
/// </summary>
public static class HibernationService
{
    private static readonly object Gate = new();

    public static List<HibernationRecord> Hibernated()
    {
        lock (Gate) return JsonStore.Load<HibernationData>(AppPaths.HibernationFile).Records;
    }

    public static bool IsHibernated(string programId) => Hibernated().Any(r => r.ProgramId == programId);

    public static (bool Success, string Message) Hibernate(InstalledProgram program, SystemFootprint footprint, bool closeRunning)
    {
        var items = FootprintBuilder.BackgroundItems(program, footprint);
        if (items.Count == 0) return (false, $"{program.DisplayName} has nothing running in the background.");

        var done = new List<ToggleItem>();
        foreach (var item in items)
        {
            if (ToggleService.Disable(item, remember: false)) done.Add(item);
        }

        if (closeRunning && ProgramInventory.GuessInstallFolder(program) is { } folder)
        {
            foreach (var p in ProcessService.FindUnder(folder)) ProcessService.Kill(p.Id);
        }

        if (done.Count == 0) return (false, "Nothing could be switched off (try running WinAppClear as administrator).");
        lock (Gate)
        {
            var data = JsonStore.Load<HibernationData>(AppPaths.HibernationFile);
            var existing = data.Records.FirstOrDefault(r => r.ProgramId == program.Id);
            if (existing is not null) existing.Items.AddRange(done.Where(d => existing.Items.All(e => e.Key != d.Key)));
            else
            {
                data.Records.Add(new HibernationRecord
                {
                    ProgramId = program.Id, ProgramName = program.DisplayName,
                    InstallFolder = ProgramInventory.GuessInstallFolder(program), Items = done,
                });
            }
            JsonStore.Save(AppPaths.HibernationFile, data);
        }
        Log.Info($"Hibernated {program.DisplayName}: {done.Count}/{items.Count} items");
        return (true, $"{program.DisplayName} is asleep ({done.Count} background item(s) switched off).");
    }

    public static (bool Success, string Message) Wake(HibernationRecord record)
    {
        var failed = 0;
        foreach (var item in record.Items)
        {
            if (!ToggleService.Enable(item)) failed++;
        }
        lock (Gate)
        {
            var data = JsonStore.Load<HibernationData>(AppPaths.HibernationFile);
            data.Records.RemoveAll(r => r.ProgramId == record.ProgramId);
            JsonStore.Save(AppPaths.HibernationFile, data);
        }
        Log.Info($"Woke {record.ProgramName}: {failed} failures");
        return failed == 0
            ? (true, $"{record.ProgramName} is awake again.")
            : (false, $"{record.ProgramName} is awake, but {failed} item(s) could not be switched back on.");
    }
}
