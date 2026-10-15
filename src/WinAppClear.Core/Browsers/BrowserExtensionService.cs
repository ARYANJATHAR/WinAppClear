using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Browsers;

public enum BrowserKind
{
    Chrome,
    Edge,
    Brave,
    Vivaldi,
    Firefox,
}

public sealed record BrowserProfile(BrowserKind Browser, string ProfileName, string ProfilePath);

public sealed record BrowserExtension(
    BrowserKind Browser,
    string Profile,
    string ProfilePath,
    string Id,
    string Name,
    string Version,
    string? Description,
    string Folder,
    bool Enabled,
    IReadOnlyList<string> Permissions)
{
    public string BrowserName => Browser.ToString();
    public bool IsChromium => Browser != BrowserKind.Firefox;
    public string StoreUrl => Browser switch
    {
        BrowserKind.Firefox => $"https://addons.mozilla.org/firefox/addon/{Id}",
        BrowserKind.Edge => $"https://microsoftedge.microsoft.com/addons/detail/{Id}",
        _ => $"https://chromewebstore.google.com/detail/{Id}",
    };
    public bool HasBroadAccess => Permissions.Any(p => p is "<all_urls>" or "*://*/*" or "http://*/*" or "https://*/*" or "tabs" or "webRequest" or "proxy" or "management");
}

/// <summary>Lists browser extensions for Chromium browsers and Firefox and removes them (browser must be closed).</summary>
public static class BrowserExtensionService
{
    private static readonly (BrowserKind Kind, string UserData, string Process)[] Chromium =
    [
        (BrowserKind.Chrome, Path.Combine(KnownPaths.LocalAppData, @"Google\Chrome\User Data"), "chrome"),
        (BrowserKind.Edge, Path.Combine(KnownPaths.LocalAppData, @"Microsoft\Edge\User Data"), "msedge"),
        (BrowserKind.Brave, Path.Combine(KnownPaths.LocalAppData, @"BraveSoftware\Brave-Browser\User Data"), "brave"),
        (BrowserKind.Vivaldi, Path.Combine(KnownPaths.LocalAppData, @"Vivaldi\User Data"), "vivaldi"),
    ];

    public static string ProcessName(BrowserKind kind) =>
        kind == BrowserKind.Firefox ? "firefox" : Chromium.First(c => c.Kind == kind).Process;

    public static bool IsBrowserRunning(BrowserKind kind) => Process.GetProcessesByName(ProcessName(kind)).Length > 0;

    public static List<BrowserProfile> GetProfiles()
    {
        var list = new List<BrowserProfile>();
        foreach (var (kind, userData, _) in Chromium)
        {
            if (!Directory.Exists(userData)) continue;
            foreach (var dir in Directory.EnumerateDirectories(userData))
            {
                var name = Path.GetFileName(dir);
                if (name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal))
                {
                    if (!File.Exists(Path.Combine(dir, "Preferences"))) continue;
                    var display = ProfileDisplayName(dir);
                    // Several profiles often share a generic name ("Your Chrome"), so add the folder name.
                    var label = string.IsNullOrWhiteSpace(display) || display == name ? name : $"{display} ({name})";
                    list.Add(new BrowserProfile(kind, label, dir));
                }
            }
        }

        var ffRoot = Path.Combine(KnownPaths.AppData, @"Mozilla\Firefox");
        var ini = Path.Combine(ffRoot, "profiles.ini");
        if (File.Exists(ini))
        {
            string? name = null;
            string? path = null;
            var relative = true;
            foreach (var raw in File.ReadAllLines(ini).Append("[end]"))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    if (path is not null)
                    {
                        var full = relative ? Path.Combine(ffRoot, path.Replace('/', '\\')) : path;
                        if (Directory.Exists(full)) list.Add(new BrowserProfile(BrowserKind.Firefox, name ?? Path.GetFileName(full), full));
                    }
                    name = null;
                    path = null;
                    relative = true;
                    continue;
                }
                if (line.StartsWith("Name=", StringComparison.Ordinal)) name = line[5..];
                else if (line.StartsWith("Path=", StringComparison.Ordinal)) path = line[5..];
                else if (line.StartsWith("IsRelative=", StringComparison.Ordinal)) relative = line.EndsWith('1');
            }
        }
        return list;
    }

    private static string? ProfileDisplayName(string profileDir)
    {
        try
        {
            var prefs = JsonNode.Parse(File.ReadAllText(Path.Combine(profileDir, "Preferences")));
            return prefs?["profile"]?["name"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public static List<BrowserExtension> GetExtensions()
    {
        var list = new List<BrowserExtension>();
        foreach (var profile in GetProfiles())
        {
            try
            {
                if (profile.Browser == BrowserKind.Firefox) list.AddRange(ReadFirefox(profile));
                else list.AddRange(ReadChromium(profile));
            }
            catch (Exception ex)
            {
                Log.Warn($"Read extensions for {profile.Browser}/{profile.ProfileName}: {ex.Message}");
            }
        }
        return list.OrderBy(e => e.Browser).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static IEnumerable<BrowserExtension> ReadChromium(BrowserProfile profile)
    {
        var extRoot = Path.Combine(profile.ProfilePath, "Extensions");
        if (!Directory.Exists(extRoot)) yield break;
        var states = ReadChromiumStates(profile.ProfilePath);
        foreach (var idDir in Directory.EnumerateDirectories(extRoot))
        {
            var id = Path.GetFileName(idDir);
            if (id == "Temp") continue;
            var versionDir = Directory.EnumerateDirectories(idDir).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
            if (versionDir is null) continue;
            var ext = ReadChromiumManifest(profile, id, versionDir, states.TryGetValue(id, out var enabled) ? enabled : true);
            if (ext is not null) yield return ext;
        }
    }

    internal static BrowserExtension? ReadChromiumManifest(BrowserProfile profile, string id, string versionDir, bool enabled)
    {
        var manifestPath = Path.Combine(versionDir, "manifest.json");
        if (!File.Exists(manifestPath)) return null;
        JsonNode? manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            });
        }
        catch
        {
            return null;
        }
        if (manifest is null) return null;
        var defaultLocale = manifest["default_locale"]?.GetValue<string>();
        var name = Localize(manifest["name"]?.GetValue<string>(), versionDir, defaultLocale) ?? id;
        var description = Localize(manifest["description"]?.GetValue<string>(), versionDir, defaultLocale);
        var perms = new List<string>();
        foreach (var key in new[] { "permissions", "host_permissions", "optional_permissions" })
        {
            if (manifest[key] is JsonArray arr)
                perms.AddRange(arr.Select(p => p?.ToString()).OfType<string>());
        }
        return new BrowserExtension(profile.Browser, profile.ProfileName, profile.ProfilePath, id, name,
            manifest["version"]?.GetValue<string>() ?? "", description, Path.GetDirectoryName(versionDir)!, enabled,
            perms.Distinct().ToList());
    }

    /// <summary>Resolves "__MSG_appName__" from _locales/&lt;locale&gt;/messages.json.</summary>
    internal static string? Localize(string? value, string extensionDir, string? defaultLocale)
    {
        if (value is null || !value.StartsWith("__MSG_", StringComparison.Ordinal) || !value.EndsWith("__", StringComparison.Ordinal))
            return value;
        var key = value[6..^2];
        foreach (var locale in new[] { "en", "en_US", "en_GB", defaultLocale }.OfType<string>().Distinct())
        {
            var file = Path.Combine(extensionDir, "_locales", locale, "messages.json");
            if (!File.Exists(file)) continue;
            try
            {
                var messages = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                if (messages is null) continue;
                foreach (var (k, v) in messages)
                {
                    if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                        return v?["message"]?.GetValue<string>() ?? value;
                }
            }
            catch
            {
                // Try the next locale.
            }
        }
        return value;
    }

    private static Dictionary<string, bool> ReadChromiumStates(string profileDir)
    {
        var map = new Dictionary<string, bool>();
        foreach (var file in new[] { "Secure Preferences", "Preferences" })
        {
            try
            {
                var path = Path.Combine(profileDir, file);
                if (!File.Exists(path)) continue;
                var settings = JsonNode.Parse(File.ReadAllText(path))?["extensions"]?["settings"]?.AsObject();
                if (settings is null) continue;
                foreach (var (id, node) in settings)
                {
                    if (node is null) continue;
                    var disableReasons = node["disable_reasons"];
                    var state = node["state"]?.GetValue<int>();
                    var disabled = (disableReasons is JsonArray a && a.Count > 0) ||
                                   (disableReasons is JsonValue v && v.TryGetValue<int>(out var r) && r != 0) ||
                                   state == 0;
                    map[id] = !disabled;
                }
            }
            catch
            {
                // Ignore unreadable preferences.
            }
        }
        return map;
    }

    public static IEnumerable<BrowserExtension> ReadFirefox(BrowserProfile profile)
    {
        var file = Path.Combine(profile.ProfilePath, "extensions.json");
        if (!File.Exists(file)) yield break;
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(file)); } catch { yield break; }
        if (root?["addons"] is not JsonArray addons) yield break;
        foreach (var a in addons)
        {
            if (a is null) continue;
            if (a["type"]?.GetValue<string>() != "extension") continue;
            var location = a["location"]?.GetValue<string>();
            if (location is not "app-profile") continue; // skip built-in/system add-ons
            var id = a["id"]?.GetValue<string>() ?? "";
            var locale = a["defaultLocale"];
            var perms = new List<string>();
            if (a["userPermissions"]?["permissions"] is JsonArray p) perms.AddRange(p.Select(x => x?.ToString()).OfType<string>());
            if (a["userPermissions"]?["origins"] is JsonArray o) perms.AddRange(o.Select(x => x?.ToString()).OfType<string>());
            yield return new BrowserExtension(BrowserKind.Firefox, profile.ProfileName, profile.ProfilePath, id,
                locale?["name"]?.GetValue<string>() ?? id, a["version"]?.GetValue<string>() ?? "",
                locale?["description"]?.GetValue<string>(), a["path"]?.GetValue<string>() ?? "",
                a["active"]?.GetValue<bool>() ?? true, perms);
        }
    }

    /// <summary>Opens the browser's own extension page so the user can remove it the official way.</summary>
    public static void OpenManagePage(BrowserExtension ext)
    {
        var exe = ext.Browser switch
        {
            BrowserKind.Edge => "msedge",
            BrowserKind.Chrome => "chrome",
            BrowserKind.Brave => "brave",
            BrowserKind.Vivaldi => "vivaldi",
            _ => "firefox",
        };
        var url = ext.Browser switch
        {
            BrowserKind.Firefox => "about:addons",
            BrowserKind.Edge => $"edge://extensions/?id={ext.Id}",
            _ => $"chrome://extensions/?id={ext.Id}",
        };
        try
        {
            Process.Start(new ProcessStartInfo(exe, url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Open extension page: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes an extension's files and preference entries. The browser must be closed, otherwise it would
    /// simply restore them. Files are backed up first.
    /// </summary>
    public static (bool Success, string Message) Remove(BrowserExtension ext)
    {
        if (IsBrowserRunning(ext.Browser)) return (false, $"Close {ext.BrowserName} first, then try again.");

        var session = Cleanup.BackupService.Begin($"Browser extension: {ext.Name} ({ext.BrowserName})");
        try
        {
            if (ext.Browser == BrowserKind.Firefox)
            {
                var xpi = string.IsNullOrEmpty(ext.Folder) ? Path.Combine(ext.ProfilePath, "extensions", ext.Id + ".xpi") : ext.Folder;
                if (File.Exists(xpi) || Directory.Exists(xpi))
                {
                    if (Cleanup.BackupService.BackupPath(session, Models.LeftoverKind.File, xpi, out var err) is null)
                        return (false, $"Could not remove the add-on file: {err}");
                }
                // Firefox notices the missing file on next start and drops the add-on from extensions.json.
                return (true, "Removed. Firefox will finish cleaning up next time it starts.");
            }

            if (Directory.Exists(ext.Folder) &&
                Cleanup.BackupService.BackupPath(session, Models.LeftoverKind.Folder, ext.Folder, out var error) is null)
                return (false, $"Could not remove the extension folder: {error}");

            // Only the plain Preferences file is edited. "Secure Preferences" is signed by the browser;
            // touching it would make the browser reset the user's settings.
            var prefsPath = Path.Combine(ext.ProfilePath, "Preferences");
            if (File.Exists(prefsPath))
            {
                var json = JsonNode.Parse(File.ReadAllText(prefsPath));
                if (json?["extensions"]?["settings"]?.AsObject() is { } settings && settings.Remove(ext.Id))
                {
                    File.Copy(prefsPath, Path.Combine(session.Folder, "Preferences.bak"), overwrite: true);
                    File.WriteAllText(prefsPath, json.ToJsonString());
                }
            }
            return (true, $"Removed. If {ext.BrowserName} lists it as corrupted, click Remove there once.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Remove extension {ext.Id}: {ex.Message}");
            return (false, ex.Message);
        }
    }
}
