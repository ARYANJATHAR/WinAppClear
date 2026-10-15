using System.Runtime.InteropServices;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Win;

public sealed record FirewallRuleInfo(string Name, string? ApplicationPath, bool Enabled, bool Inbound, bool Allow, string? Grouping);

/// <summary>Reads and changes Windows Firewall rules through the HNetCfg.FwPolicy2 COM API.</summary>
public static class FirewallService
{
    public static List<FirewallRuleInfo> GetRules()
    {
        var list = new List<FirewallRuleInfo>();
        dynamic? policy = null;
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    string? app = rule.ApplicationName;
                    if (string.IsNullOrWhiteSpace(app)) continue;
                    list.Add(new FirewallRuleInfo((string)rule.Name, PathUtil.Normalize(app), (bool)rule.Enabled,
                        (int)rule.Direction == 1, (int)rule.Action == 1, (string?)rule.Grouping));
                }
                catch
                {
                    // Skip unreadable rule.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Firewall enumeration failed: {ex.Message}");
        }
        finally
        {
            if (policy is not null) Marshal.FinalReleaseComObject(policy);
        }
        return list;
    }

    /// <summary>Enables or disables every rule with this name that points at <paramref name="appPath"/>.</summary>
    public static bool SetEnabled(string name, string? appPath, bool enabled)
    {
        dynamic? policy = null;
        var changed = false;
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            foreach (dynamic rule in policy.Rules)
            {
                if ((string)rule.Name != name) continue;
                if (appPath is not null && !string.Equals(PathUtil.Normalize((string?)rule.ApplicationName), appPath,
                        StringComparison.OrdinalIgnoreCase)) continue;
                rule.Enabled = enabled;
                changed = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Firewall set {name}: {ex.Message}");
        }
        finally
        {
            if (policy is not null) Marshal.FinalReleaseComObject(policy);
        }
        return changed;
    }

    /// <summary>Removes rules with this name, but only if every rule with the name targets <paramref name="appPath"/>.</summary>
    public static bool Remove(string name, string appPath)
    {
        var all = GetRules().Where(r => r.Name == name).ToList();
        if (all.Count == 0) return true;
        if (all.Any(r => !string.Equals(r.ApplicationPath, appPath, StringComparison.OrdinalIgnoreCase)))
        {
            Log.Warn($"Firewall rule '{name}' is shared with other applications — disabling instead of removing.");
            return SetEnabled(name, appPath, false);
        }
        dynamic? policy = null;
        try
        {
            policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            for (var i = 0; i < all.Count; i++) policy.Rules.Remove(name);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Firewall remove {name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (policy is not null) Marshal.FinalReleaseComObject(policy);
        }
    }
}
