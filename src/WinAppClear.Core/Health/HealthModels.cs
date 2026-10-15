using WinAppClear.Core.Inventory;
using WinAppClear.Core.Leftovers;
using WinAppClear.Core.Models;

namespace WinAppClear.Core.Health;

public enum HealthCategory
{
    Leftovers,
    Outdated,
    Permissions,
    Notifications,
    UninstallIssues,
    InstallationFiles,
    Malicious,
    RedundantFiles,
    Hibernation,
}

public enum HealthSeverity
{
    Info,
    Warning,
    Danger,
}

/// <summary>One finding from a Software Health check.</summary>
public sealed class HealthIssue
{
    public required HealthCategory Category { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public long? SizeBytes { get; init; }
    public HealthSeverity Severity { get; init; } = HealthSeverity.Info;

    /// <summary>Whether the item is ticked by default in the UI.</summary>
    public bool Recommended { get; init; }

    /// <summary>Check-specific data needed to fix the issue.</summary>
    public object? Payload { get; init; }
}

public sealed record HealthFixResult(int Fixed, int Failed, List<string> Messages, string? BackupId = null)
{
    public static HealthFixResult Empty => new(0, 0, []);
}

/// <summary>Shared, lazily-loaded system state so checks don't each re-read Windows.</summary>
public sealed class HealthContext
{
    private readonly Lazy<List<InstalledProgram>> _programs;

    public HealthContext(List<InstalledProgram>? programs = null)
    {
        _programs = new Lazy<List<InstalledProgram>>(() =>
        {
            var list = programs ?? ProgramInventory.GetInstalledPrograms();
            if (list.All(p => p.LastUsed is null)) UsageTracker.Apply(list, UsageTracker.ReadLastRunTimes());
            return list;
        });
    }

    public List<InstalledProgram> Programs => _programs.Value;
    public SystemFootprint Footprint { get; } = new();
}

public interface IHealthCheck
{
    HealthCategory Category { get; }
    string Title { get; }
    string Description { get; }
    string FixLabel { get; }
    Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct);
    Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct);
}
