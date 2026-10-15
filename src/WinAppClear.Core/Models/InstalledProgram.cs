using Microsoft.Win32;

namespace WinAppClear.Core.Models;

/// <summary>A desktop program registered under one of Windows' Uninstall registry keys.</summary>
public sealed class InstalledProgram
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? DisplayVersion { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? ModifyPath { get; init; }
    public string? DisplayIcon { get; init; }
    public string? UrlInfoAbout { get; init; }
    public bool IsWindowsInstaller { get; init; }
    public bool NoRepair { get; init; }
    public bool NoModify { get; init; }
    public string? MsiProductCode { get; init; }

    /// <summary>Install date from the InstallDate value, falling back to the registry key's last write time.</summary>
    public DateTime? InstallDate { get; init; }

    /// <summary>Last write time of the uninstall key; gives minute-level install time.</summary>
    public DateTime? RegistryTimestamp { get; init; }

    /// <summary>EstimatedSize from the registry, in bytes (may be missing or stale).</summary>
    public long? EstimatedSizeBytes { get; init; }

    /// <summary>Full registry path of the uninstall key, e.g. HKLM\SOFTWARE\...\Uninstall\Foo.</summary>
    public required string RegistryKeyPath { get; init; }
    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string KeyName { get; init; }

    public bool Is64Bit => View == RegistryView.Registry64 && Hive == RegistryHive.LocalMachine;
    public bool IsPerUser => Hive == RegistryHive.CurrentUser;

    /// <summary>Folder size measured on disk (filled in lazily).</summary>
    public long? MeasuredSizeBytes { get; set; }

    /// <summary>Last time the program was run, from UserAssist / Prefetch (filled in lazily).</summary>
    public DateTime? LastUsed { get; set; }

    public long? SizeBytes => MeasuredSizeBytes ?? EstimatedSizeBytes;

    public string ArchitectureLabel => IsPerUser ? "User" : Is64Bit ? "64-bit" : "32-bit";

    public override string ToString() => $"{DisplayName} {DisplayVersion}".Trim();
}
