using System.Text;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Updates;

public sealed record AvailableUpdate(string Name, string Id, string InstalledVersion, string AvailableVersion, string Source);

/// <summary>Finds and installs software updates through the Windows Package Manager (winget).</summary>
public static class WingetClient
{
    public static string? FindWinget()
    {
        var alias = Path.Combine(KnownPaths.LocalAppData, @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Bad PATH entry.
            }
        }
        return null;
    }

    public static bool IsAvailable => FindWinget() is not null;

    public static async Task<(List<AvailableUpdate> Updates, string? Error)> GetUpdatesAsync(CancellationToken ct = default)
    {
        var winget = FindWinget();
        if (winget is null) return ([], "winget (App Installer) is not installed. Get it from the Microsoft Store.");
        var r = await ProcessRunner.RunAsync(winget,
            "upgrade --include-unknown --accept-source-agreements --disable-interactivity", ct, Encoding.UTF8);
        var updates = Parse(r.Output);
        if (updates.Count == 0 && r.ExitCode != 0 && !r.Output.Contains("No installed package", StringComparison.OrdinalIgnoreCase))
        {
            var lastLine = r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? r.Error;
            if (!string.IsNullOrWhiteSpace(lastLine) && !lastLine.Contains("upgrades available", StringComparison.OrdinalIgnoreCase))
                return (updates, lastLine);
        }
        return (updates, null);
    }

    /// <summary>Parses winget's fixed-width table output (handles spinner/progress noise and truncated names).</summary>
    public static List<AvailableUpdate> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .ToList();

        var headerIdx = lines.FindIndex(l =>
            l.Contains("Name", StringComparison.Ordinal) && l.Contains("Id", StringComparison.Ordinal) &&
            l.Contains("Version", StringComparison.Ordinal) && l.Contains("Available", StringComparison.Ordinal));
        if (headerIdx < 0) return [];

        var header = lines[headerIdx];
        var nameCol = header.IndexOf("Name", StringComparison.Ordinal);
        var idCol = header.IndexOf("Id", StringComparison.Ordinal);
        var versionCol = header.IndexOf("Version", StringComparison.Ordinal);
        var availableCol = header.IndexOf("Available", StringComparison.Ordinal);
        var sourceCol = header.IndexOf("Source", StringComparison.Ordinal);

        var list = new List<AvailableUpdate>();
        foreach (var line in lines.Skip(headerIdx + 1))
        {
            if (line.Trim().Length == 0) continue;
            if (line.TrimStart().StartsWith("---", StringComparison.Ordinal)) continue;
            if (line.Length < availableCol) break; // summary line ("N upgrades available.")
            if (char.IsDigit(line.TrimStart()[0]) && line.Contains("upgrade", StringComparison.OrdinalIgnoreCase)) break;

            // Widths are measured in display columns; CJK/wide characters can shift them, so re-anchor
            // on whitespace runs when the fixed columns don't line up.
            string Col(int start, int end) =>
                start >= line.Length ? "" : line[start..Math.Min(end < 0 ? line.Length : end, line.Length)].Trim();

            var name = Col(nameCol, idCol);
            var id = Col(idCol, versionCol);
            var installed = Col(versionCol, availableCol);
            var available = Col(availableCol, sourceCol);
            var source = sourceCol >= 0 ? Col(sourceCol, -1) : "";
            if (id.Contains(' '))
            {
                // Misaligned row — fall back to splitting from the right.
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    source = sourceCol >= 0 ? parts[^1] : "";
                    var offset = sourceCol >= 0 ? 1 : 0;
                    available = parts[^(1 + offset)];
                    installed = parts[^(2 + offset)];
                    id = parts[^(3 + offset)];
                    name = string.Join(' ', parts[..^(3 + offset)]);
                }
            }
            if (id.Length == 0 || available.Length == 0) continue;
            list.Add(new AvailableUpdate(name.TrimEnd('…'), id, installed, available, source));
        }
        return list;
    }

    public static async Task<(bool Success, string Output)> UpgradeAsync(string id, CancellationToken ct = default)
    {
        var winget = FindWinget();
        if (winget is null) return (false, "winget is not installed.");
        var r = await ProcessRunner.RunAsync(winget,
            $"upgrade --id \"{id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            ct, Encoding.UTF8);
        var tail = string.Join('\n', r.Output.Split('\n').Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l)
            .Where(l => l.Trim().Length > 0).TakeLast(3));
        Log.Info($"winget upgrade {id}: exit {r.ExitCode}");
        return (r.ExitCode == 0, tail);
    }
}
