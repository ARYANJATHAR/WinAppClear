using WinAppClear.Core.Models;

namespace WinAppClear.Core.Cleanup;

public sealed class BackupEntry
{
    public required LeftoverKind Kind { get; init; }
    public required string OriginalPath { get; init; }
    public string? ValueName { get; init; }
    public string? RegistryView { get; init; }

    /// <summary>Path inside the backup folder (moved files/folders, .reg exports, task XML).</summary>
    public string? BackupFile { get; set; }

    /// <summary>For registry values: the value kind and data (base64 for binary).</summary>
    public string? ValueKind { get; set; }
    public string? ValueData { get; set; }
    /// <summary>Null for legacy backups; "json" preserves empty multi-string elements.</summary>
    public string? ValueDataFormat { get; set; }

    /// <summary>For services: the start mode before deletion (informational).</summary>
    public string? Note { get; set; }

    public bool Restorable { get; set; } = true;
    public bool Restored { get; set; }
}

public sealed class BackupSession
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public List<BackupEntry> Entries { get; init; } = [];
    public bool Restored { get; set; }

    public string Folder => Path.Combine(Util.AppPaths.Backups, Id);
}
