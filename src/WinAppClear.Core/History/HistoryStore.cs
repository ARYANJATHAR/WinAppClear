using WinAppClear.Core.Models;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.History;

public enum RemovalMethod
{
    Standard,
    Quiet,
    Forced,
    StoreApp,
    BrowserExtension,
    InstallLog,
    AndroidApp,
    BrokenEntry,
}

public sealed class HistoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime Date { get; init; } = DateTime.Now;
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? InstallFolder { get; init; }
    public RemovalMethod Method { get; init; }
    public string Result { get; set; } = "";
    public int? ExitCode { get; set; }
    public int LeftoversFound { get; set; }
    public int LeftoversRemoved { get; set; }
    public long BytesFreed { get; set; }
    public string? BackupId { get; set; }

    /// <summary>Leftovers the user chose to keep or that could not be removed — re-checked by Software Health.</summary>
    public List<LeftoverItem> RemainingLeftovers { get; set; } = [];
}

public sealed class HistoryData
{
    public List<HistoryEntry> Entries { get; set; } = [];
}

/// <summary>Uninstall history, newest first, persisted to history.json.</summary>
public static class HistoryStore
{
    private static readonly object Gate = new();

    public static List<HistoryEntry> All()
    {
        lock (Gate)
        {
            return JsonStore.Load<HistoryData>(AppPaths.HistoryFile).Entries.OrderByDescending(e => e.Date).ToList();
        }
    }

    public static void Add(HistoryEntry entry)
    {
        lock (Gate)
        {
            var data = JsonStore.Load<HistoryData>(AppPaths.HistoryFile);
            data.Entries.Add(entry);
            JsonStore.Save(AppPaths.HistoryFile, data);
        }
    }

    public static void Update(HistoryEntry entry)
    {
        lock (Gate)
        {
            var data = JsonStore.Load<HistoryData>(AppPaths.HistoryFile);
            var idx = data.Entries.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0) data.Entries[idx] = entry;
            else data.Entries.Add(entry);
            JsonStore.Save(AppPaths.HistoryFile, data);
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            var data = JsonStore.Load<HistoryData>(AppPaths.HistoryFile);
            data.Entries.RemoveAll(e => e.Id == id);
            JsonStore.Save(AppPaths.HistoryFile, data);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            JsonStore.Save(AppPaths.HistoryFile, new HistoryData());
        }
    }
}
