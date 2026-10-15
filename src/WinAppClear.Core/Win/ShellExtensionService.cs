using WinAppClear.Core.Util;
using Microsoft.Win32;

namespace WinAppClear.Core.Win;

public sealed record ShellExtensionInfo(string Clsid, string HandlerName, string Scope, string? DllPath, bool Blocked);

/// <summary>
/// Lists right-click (context menu) shell extensions and blocks/unblocks them via the official
/// "Shell Extensions\Blocked" list, which is fully reversible.
/// </summary>
public static class ShellExtensionService
{
    private const string BlockedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private static readonly string[] Scopes =
    [
        "*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive",
    ];

    public static List<ShellExtensionInfo> GetContextMenuHandlers()
    {
        var blocked = BlockedClsids();
        var list = new List<ShellExtensionInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
        foreach (var scope in Scopes)
        {
            using var handlers = classes.OpenSubKey($@"{scope}\shellex\ContextMenuHandlers");
            if (handlers is null) continue;
            foreach (var name in handlers.GetSubKeyNames())
            {
                try
                {
                    using var h = handlers.OpenSubKey(name);
                    var clsid = h?.GetValue("") as string;
                    if (string.IsNullOrWhiteSpace(clsid) || !clsid.StartsWith('{')) clsid = name.StartsWith('{') ? name : null;
                    if (clsid is null || !seen.Add(scope + clsid)) continue;
                    using var server = classes.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
                    var dll = PathUtil.Normalize(server?.GetValue("") as string);
                    list.Add(new ShellExtensionInfo(clsid, name, scope, dll, blocked.Contains(clsid)));
                }
                catch
                {
                    // Skip broken handler.
                }
            }
        }
        return list;
    }

    private static HashSet<string> BlockedClsids()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(BlockedKey);
            if (key is null) continue;
            foreach (var v in key.GetValueNames()) set.Add(v);
        }
        return set;
    }

    public static bool SetBlocked(string clsid, bool blocked)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .CreateSubKey(BlockedKey, writable: true);
            if (blocked) key.SetValue(clsid, "Blocked by WinAppClear", RegistryValueKind.String);
            else key.DeleteValue(clsid, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Shell extension block {clsid}={blocked}: {ex.Message}");
            return false;
        }
    }
}
