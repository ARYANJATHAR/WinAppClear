using System.Text.Json;
using System.Text.RegularExpressions;
using WinAppClear.Core.Browsers;
using WinAppClear.Core.Models;
using WinAppClear.Core.Uninstall;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Health;

public sealed class BlocklistEntry
{
    public string Pattern { get; set; } = "";
    public string Type { get; set; } = "PUP";
    public string Description { get; set; } = "";
}

public sealed class Blocklist
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonStore.Options)
    {
        PropertyNameCaseInsensitive = true,
    };

    public int Version { get; set; }
    public string? Note { get; set; }
    public List<BlocklistEntry> Entries { get; set; } = [];

    /// <summary>Built-in list (embedded resource) plus the user's own additions.</summary>
    public static Blocklist Load()
    {
        var list = new Blocklist();
        try
        {
            using var stream = typeof(Blocklist).Assembly.GetManifestResourceStream("WinAppClear.Core.Data.blocklist.json");
            if (stream is not null)
            {
                var builtIn = JsonSerializer.Deserialize<Blocklist>(stream, ReadOptions);
                if (builtIn is not null) list.Entries.AddRange(builtIn.Entries);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Built-in blocklist", ex);
        }

        try
        {
            if (File.Exists(AppPaths.UserBlocklistFile))
            {
                var user = JsonSerializer.Deserialize<Blocklist>(File.ReadAllText(AppPaths.UserBlocklistFile), ReadOptions);
                if (user is not null) list.Entries.AddRange(user.Entries);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"User blocklist: {ex.Message}");
        }
        return list;
    }

    public BlocklistEntry? Match(params string?[] values)
    {
        foreach (var entry in Entries)
        {
            try
            {
                var regex = new Regex(entry.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                if (values.Any(v => !string.IsNullOrEmpty(v) && regex.IsMatch(v))) return entry;
            }
            catch
            {
                // Bad user pattern — ignore it.
            }
        }
        return null;
    }
}

/// <summary>7. Known adware / potentially unwanted programs and browser extensions.</summary>
public sealed class MaliciousCheck : IHealthCheck
{
    public HealthCategory Category => HealthCategory.Malicious;
    public string Title => "Malicious Software & Extensions";
    public string Description => "Known adware, browser hijackers and potentially unwanted programs. Basic list — not a replacement for antivirus.";
    public string FixLabel => "Remove";

    public sealed record ProgramHit(InstalledProgram Program, BlocklistEntry Entry);
    public sealed record ExtensionHit(BrowserExtension Extension, BlocklistEntry Entry);

    public Task<List<HealthIssue>> ScanAsync(HealthContext context, CancellationToken ct) => Task.Run(() =>
    {
        var blocklist = Blocklist.Load();
        var issues = new List<HealthIssue>();
        foreach (var p in context.Programs)
        {
            var hit = blocklist.Match(p.DisplayName, p.Publisher);
            if (hit is null) continue;
            issues.Add(new HealthIssue
            {
                Category = Category, Title = p.DisplayName, Detail = $"{hit.Type}: {hit.Description}",
                Severity = HealthSeverity.Danger, Recommended = true, Payload = new ProgramHit(p, hit),
            });
        }
        foreach (var ext in BrowserExtensionService.GetExtensions())
        {
            var hit = blocklist.Match(ext.Name, ext.Description);
            if (hit is null) continue;
            issues.Add(new HealthIssue
            {
                Category = Category, Title = $"{ext.Name} ({ext.BrowserName} extension)", Detail = $"{hit.Type}: {hit.Description}",
                Severity = HealthSeverity.Danger, Recommended = true, Payload = new ExtensionHit(ext, hit),
            });
        }
        return issues;
    }, ct);

    public async Task<HealthFixResult> FixAsync(IReadOnlyList<HealthIssue> issues, IProgress<string>? progress, CancellationToken ct)
    {
        int ok = 0, failed = 0;
        var messages = new List<string>();
        foreach (var payload in issues.Select(i => i.Payload))
        {
            ct.ThrowIfCancellationRequested();
            switch (payload)
            {
                case ProgramHit hit:
                {
                    var entry = await UninstallWorkflow.RunUnattendedAsync(hit.Program,
                        new UninstallOptions { Quiet = false, CreateRestorePoint = true, AutoRemoveHighConfidence = true },
                        progress, ct);
                    if (!Inventory.ProgramInventory.StillInstalled(hit.Program)) ok++;
                    else
                    {
                        failed++;
                        messages.Add($"{hit.Program.DisplayName}: {entry.Result}");
                    }
                    break;
                }
                case ExtensionHit eh:
                {
                    var (success, message) = BrowserExtensionService.Remove(eh.Extension);
                    if (success) ok++;
                    else
                    {
                        failed++;
                        messages.Add($"{eh.Extension.Name}: {message}");
                    }
                    break;
                }
            }
        }
        return new HealthFixResult(ok, failed, messages);
    }
}
