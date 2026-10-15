using System.Text.RegularExpressions;
using WinAppClear.Core.Models;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Uninstall;

public enum InstallerType
{
    Unknown,
    Msi,
    Inno,
    Nsis,
    InstallShield,
    Wix,
}

/// <summary>A resolved command line: executable plus arguments.</summary>
public sealed record CommandLine(string FileName, string Arguments)
{
    public override string ToString() => string.IsNullOrEmpty(Arguments) ? $"\"{FileName}\"" : $"\"{FileName}\" {Arguments}";
}

/// <summary>Turns registry uninstall strings into runnable commands, including silent variants.</summary>
public static partial class UninstallCommand
{
    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoName();

    /// <summary>Splits "\"C:\a b\x.exe\" /arg" or unquoted "C:\a b\x.exe /arg" into exe + args.</summary>
    public static CommandLine? Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var cmd = Environment.ExpandEnvironmentVariables(commandLine.Trim());

        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end < 0) return new CommandLine(cmd.Trim('"'), "");
            return new CommandLine(cmd[1..end], cmd[(end + 1)..].Trim());
        }

        // Unquoted: prefer the shortest prefix ending in ".exe" that exists on disk,
        // otherwise the first ".exe" followed by a space or the end of the string.
        CommandLine? firstGuess = null;
        for (var idx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
             idx >= 0;
             idx = cmd.IndexOf(".exe", idx + 4, StringComparison.OrdinalIgnoreCase))
        {
            var end = idx + 4;
            if (end < cmd.Length && cmd[end] != ' ') continue;
            var candidate = new CommandLine(cmd[..end], cmd[end..].Trim());
            if (File.Exists(candidate.FileName)) return candidate;
            firstGuess ??= candidate;
        }
        if (firstGuess is not null) return firstGuess;

        var space = cmd.IndexOf(' ');
        return space < 0 ? new CommandLine(cmd, "") : new CommandLine(cmd[..space], cmd[(space + 1)..].Trim());
    }

    /// <summary>
    /// Resolves a bare command name ("winget", "cmd.exe") through System32, the app-execution-alias folder and PATH.
    /// Returns the input unchanged when it is already a full path.
    /// </summary>
    public static string? ResolveExecutable(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName)) return File.Exists(fileName) ? fileName : null;
        var name = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe";
        var dirs = new List<string> { KnownPaths.System32, KnownPaths.Windows, Path.Combine(KnownPaths.LocalAppData, @"Microsoft\WindowsApps") };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Bad PATH entry.
            }
        }
        return null;
    }

    public static bool IsMsiExec(string fileName) =>
        Path.GetFileName(fileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(fileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase);

    public static string MsiExecPath => Path.Combine(KnownPaths.System32, "msiexec.exe");

    public static InstallerType Detect(InstalledProgram p)
    {
        if (p.IsWindowsInstaller || (Parse(p.UninstallString) is { } c && IsMsiExec(c.FileName))) return InstallerType.Msi;
        var cmd = Parse(p.UninstallString);
        if (cmd is null) return InstallerType.Unknown;
        var file = Path.GetFileName(cmd.FileName);
        if (InnoName().IsMatch(file)) return InstallerType.Inno;
        if (p.KeyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase)) return InstallerType.Inno;
        if (cmd.Arguments.Contains("-runfromtemp", StringComparison.OrdinalIgnoreCase) ||
            cmd.FileName.Contains("InstallShield", StringComparison.OrdinalIgnoreCase))
            return InstallerType.InstallShield;
        if (LooksLikeNsis(cmd.FileName)) return InstallerType.Nsis;
        return InstallerType.Unknown;
    }

    private static bool LooksLikeNsis(string exe)
    {
        try
        {
            if (!File.Exists(exe)) return false;
            using var fs = File.OpenRead(exe);
            var buffer = new byte[Math.Min(fs.Length, 2 * 1024 * 1024)];
            var read = fs.Read(buffer, 0, buffer.Length);
            var text = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            return text.Contains("Nullsoft", StringComparison.Ordinal) || text.Contains("NSIS", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The normal (interactive) uninstall command.</summary>
    public static CommandLine? Normal(InstalledProgram p)
    {
        var cmd = Parse(p.UninstallString);
        if (cmd is not null && IsMsiExec(cmd.FileName))
        {
            var code = GuidPattern().Match(cmd.Arguments) is { Success: true } m ? m.Value : p.MsiProductCode;
            return code is null ? cmd : new CommandLine(MsiExecPath, $"/x {code}");
        }
        if (cmd is null && p.MsiProductCode is not null) return new CommandLine(MsiExecPath, $"/x {p.MsiProductCode}");
        return cmd;
    }

    /// <summary>A silent uninstall command, or null when this installer type has no known silent mode.</summary>
    public static CommandLine? Quiet(InstalledProgram p)
    {
        if (!string.IsNullOrWhiteSpace(p.QuietUninstallString)) return Parse(p.QuietUninstallString);

        var type = Detect(p);
        var normal = Normal(p);
        if (normal is null) return null;
        return type switch
        {
            InstallerType.Msi when normal.Arguments.Contains("/x", StringComparison.OrdinalIgnoreCase) =>
                normal with { Arguments = normal.Arguments + " /qn /norestart" },
            InstallerType.Inno => normal with { Arguments = Join(normal.Arguments, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") },
            InstallerType.Nsis => normal with { Arguments = Join(normal.Arguments, "/S") },
            _ => null,
        };
    }

    /// <summary>Command for "Repair": msiexec /fa for MSI, otherwise the ModifyPath.</summary>
    public static CommandLine? Repair(InstalledProgram p)
    {
        if (p.NoRepair && p.NoModify) return null;
        if (p.MsiProductCode is not null && Detect(p) == InstallerType.Msi)
            return new CommandLine(MsiExecPath, $"/fa {p.MsiProductCode}");
        return Parse(p.ModifyPath);
    }

    private static string Join(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : $"{a} {b}";
}
