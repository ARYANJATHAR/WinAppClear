using System.Text.Json.Serialization;

namespace WinAppClear.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeftoverKind
{
    Folder,
    File,
    RegistryKey,
    RegistryValue,
    Service,
    ScheduledTask,
    Shortcut,
    StartupEntry,
    FirewallRule,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Confidence
{
    Low,
    Medium,
    High,
}

/// <summary>Something a program left behind (or owns) that can be removed.</summary>
public sealed class LeftoverItem
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>
    /// File/folder path, full registry key path (HKLM\...), service name, task path,
    /// or firewall rule name, depending on <see cref="Kind"/>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>For registry values and startup entries stored as values: the value name.</summary>
    public string? ValueName { get; init; }

    /// <summary>For registry items: "64" or "32" registry view.</summary>
    public string? RegistryView { get; init; }

    public required Confidence Confidence { get; init; }
    public required string Reason { get; init; }
    public long? SizeBytes { get; set; }

    /// <summary>Extra detail shown to the user, e.g. a service's display name or a task's command.</summary>
    public string? Detail { get; init; }

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        LeftoverKind.RegistryKey => "Registry key",
        LeftoverKind.RegistryValue => "Registry value",
        LeftoverKind.ScheduledTask => "Scheduled task",
        LeftoverKind.StartupEntry => "Startup entry",
        LeftoverKind.FirewallRule => "Firewall rule",
        _ => Kind.ToString(),
    };

    [JsonIgnore]
    public string DisplayPath => ValueName is null ? Path : $"{Path}  [{ValueName}]";

    public string Key => $"{Kind}|{RegistryView}|{Path}|{ValueName}".ToLowerInvariant();

    public override string ToString() => $"{KindLabel}: {DisplayPath} ({Confidence}, {Reason})";
}
