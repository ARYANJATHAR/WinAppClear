using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;
using Microsoft.Win32;

namespace WinAppClear.Core.Health;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToggleKind
{
    Startup,
    Service,
    ScheduledTask,
    ShellExtension,
    FirewallRule,
    AppNotification,
    SiteNotification,
}

/// <summary>
/// A background "permission" a program has (startup entry, service, task, ...) that WinAppClear can switch off
/// and later switch back on exactly as it was.
/// </summary>
public sealed class ToggleItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required ToggleKind Kind { get; init; }
    public required string Owner { get; init; }
    public required string Display { get; init; }

    /// <summary>Kind-specific key: startup "Location|Name", service name, task path, CLSID, rule name, AUMID, site pattern.</summary>
    public required string Key { get; init; }

    /// <summary>Extra data: firewall app path, original service start mode, browser Preferences path.</summary>
    public string? Extra { get; init; }
    public string? OriginalState { get; set; }
    public DateTime? DisabledAt { get; set; }

    public string KindLabel => Kind switch
    {
        ToggleKind.Startup => "Starts with Windows",
        ToggleKind.Service => "Background service",
        ToggleKind.ScheduledTask => "Scheduled task",
        ToggleKind.ShellExtension => "Right-click menu extension",
        ToggleKind.FirewallRule => "Firewall exception",
        ToggleKind.AppNotification => "App notifications",
        ToggleKind.SiteNotification => "Website notifications",
        _ => Kind.ToString(),
    };
}

public sealed class DisabledItemsData
{
    public List<ToggleItem> Items { get; set; } = [];
}

/// <summary>Switches toggle items off/on and remembers what was switched off (disabled-items.json).</summary>
public static class ToggleService
{
    private static readonly object Gate = new();
    public const string NotificationSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    public static List<ToggleItem> DisabledItems()
    {
        lock (Gate) return JsonStore.Load<DisabledItemsData>(AppPaths.DisabledItemsFile).Items;
    }

    private static void SaveDisabled(Action<List<ToggleItem>> change)
    {
        lock (Gate)
        {
            var data = JsonStore.Load<DisabledItemsData>(AppPaths.DisabledItemsFile);
            change(data.Items);
            JsonStore.Save(AppPaths.DisabledItemsFile, data);
        }
    }

    /// <summary>Disables the item. When <paramref name="remember"/> is set it's listed under "Disabled items".</summary>
    public static bool Disable(ToggleItem item, bool remember = true)
    {
        bool ok;
        switch (item.Kind)
        {
            case ToggleKind.Startup:
            {
                var entry = FindStartup(item.Key);
                ok = entry is not null && StartupManager.SetEnabled(entry, false);
                break;
            }
            case ToggleKind.Service:
            {
                var svc = ServiceManager.GetServices().FirstOrDefault(s => s.Name.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
                if (svc is null)
                {
                    ok = false;
                    break;
                }
                item.OriginalState = $"{(int)svc.StartMode}|{svc.DelayedAutoStart}";
                ok = ServiceManager.SetStartMode(svc.Name, ServiceStartMode2.Manual);
                if (ok) ServiceManager.Stop(svc.Name);
                break;
            }
            case ToggleKind.ScheduledTask:
                ok = TaskSchedulerService.SetEnabled(item.Key, false);
                break;
            case ToggleKind.ShellExtension:
                ok = ShellExtensionService.SetBlocked(item.Key, true);
                break;
            case ToggleKind.FirewallRule:
                ok = FirewallService.SetEnabled(item.Key, item.Extra, false);
                break;
            case ToggleKind.AppNotification:
                ok = SetAppNotifications(item.Key, false);
                break;
            case ToggleKind.SiteNotification:
                ok = SetSiteNotification(item.Extra!, item.Key, allow: false, out var original);
                item.OriginalState = original;
                break;
            default:
                ok = false;
                break;
        }

        if (ok && remember)
        {
            item.DisabledAt = DateTime.Now;
            SaveDisabled(list =>
            {
                list.RemoveAll(i => i.Kind == item.Kind && i.Key == item.Key);
                list.Add(item);
            });
        }
        Log.Info($"Disable {item.Kind} '{item.Display}': {ok}");
        return ok;
    }

    public static bool Enable(ToggleItem item)
    {
        bool ok;
        switch (item.Kind)
        {
            case ToggleKind.Startup:
            {
                var entry = FindStartup(item.Key);
                ok = entry is not null && StartupManager.SetEnabled(entry, true);
                break;
            }
            case ToggleKind.Service:
            {
                var parts = (item.OriginalState ?? "2|False").Split('|');
                var mode = (ServiceStartMode2)int.Parse(parts[0]);
                var delayed = parts.Length > 1 && bool.TryParse(parts[1], out var d) && d;
                ok = ServiceManager.SetStartMode(item.Key, mode, delayed);
                if (ok && mode == ServiceStartMode2.Automatic) ServiceManager.Start(item.Key);
                break;
            }
            case ToggleKind.ScheduledTask:
                ok = TaskSchedulerService.SetEnabled(item.Key, true);
                break;
            case ToggleKind.ShellExtension:
                ok = ShellExtensionService.SetBlocked(item.Key, false);
                break;
            case ToggleKind.FirewallRule:
                ok = FirewallService.SetEnabled(item.Key, item.Extra, true);
                break;
            case ToggleKind.AppNotification:
                ok = SetAppNotifications(item.Key, true);
                break;
            case ToggleKind.SiteNotification:
                ok = SetSiteNotification(item.Extra!, item.Key, allow: true, out _);
                break;
            default:
                ok = false;
                break;
        }
        if (ok) SaveDisabled(list => list.RemoveAll(i => i.Kind == item.Kind && i.Key == item.Key));
        Log.Info($"Enable {item.Kind} '{item.Display}': {ok}");
        return ok;
    }

    private static StartupEntry? FindStartup(string key) =>
        StartupManager.GetEntries().FirstOrDefault(e => e.Id.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static bool SetAppNotifications(string appId, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{NotificationSettingsKey}\{appId}", writable: true);
            if (enabled) key.DeleteValue("Enabled", throwOnMissingValue: false);
            else key.SetValue("Enabled", 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Notifications {appId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Sets a Chromium site's notification permission (1 = allow, 2 = block) in the profile's Preferences.</summary>
    public static bool SetSiteNotification(string preferencesPath, string pattern, bool allow, out string? originalSetting)
    {
        originalSetting = null;
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(preferencesPath));
            var exceptions = json?["profile"]?["content_settings"]?["exceptions"]?["notifications"]?.AsObject();
            if (exceptions?[pattern] is not JsonObject site) return false;
            originalSetting = site["setting"]?.ToString();
            site["setting"] = allow ? 1 : 2;
            File.WriteAllText(preferencesPath, json!.ToJsonString());
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Site notification {pattern}: {ex.Message}");
            return false;
        }
    }
}
