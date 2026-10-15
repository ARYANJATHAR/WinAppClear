using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WinAppClear.Core.Util;

/// <summary>Helpers for working with registry keys addressed by full path strings like "HKLM\SOFTWARE\Foo".</summary>
public static class RegistryPath
{
    public static (RegistryHive Hive, string SubKey) Parse(string fullPath)
    {
        var idx = fullPath.IndexOf('\\');
        var root = idx < 0 ? fullPath : fullPath[..idx];
        var sub = idx < 0 ? "" : fullPath[(idx + 1)..];
        var hive = root.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
            _ => throw new ArgumentException($"Unknown registry hive in '{fullPath}'"),
        };
        return (hive, sub.TrimEnd('\\'));
    }

    public static string HiveShortName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        RegistryHive.CurrentConfig => "HKCC",
        _ => hive.ToString(),
    };

    public static string Combine(RegistryHive hive, string subKey) =>
        string.IsNullOrEmpty(subKey) ? HiveShortName(hive) : $"{HiveShortName(hive)}\\{subKey}";

    public static RegistryView ParseView(string? view) =>
        view == "32" ? RegistryView.Registry32 : RegistryView.Registry64;

    public static string ViewName(RegistryView view) => view == RegistryView.Registry32 ? "32" : "64";

    public static RegistryKey? Open(string fullPath, RegistryView view, bool writable = false)
    {
        var (hive, sub) = Parse(fullPath);
        try
        {
            var baseKey = RegistryKey.OpenBaseKey(hive, view);
            return sub.Length == 0 ? baseKey : baseKey.OpenSubKey(sub, writable);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    public static bool KeyExists(string fullPath, RegistryView view)
    {
        using var key = Open(fullPath, view);
        return key is not null;
    }

    public static bool ValueExists(string fullPath, string valueName, RegistryView view)
    {
        using var key = Open(fullPath, view);
        return key?.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase) == true;
    }

    public static void DeleteKeyTree(string fullPath, RegistryView view)
    {
        var (hive, sub) = Parse(fullPath);
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        baseKey.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
    }

    /// <summary>Exports a key (recursively) to a .reg file with reg.exe.</summary>
    public static bool Export(string fullPath, RegistryView view, string file)
    {
        var args = $"export \"{fullPath}\" \"{file}\" /y /reg:{ViewName(view)}";
        return RunReg(args);
    }

    public static bool Import(string file, RegistryView view) =>
        RunReg($"import \"{file}\" /reg:{ViewName(view)}");

    private static bool RunReg(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(KnownPaths.System32, "reg.exe"), args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            if (p.ExitCode != 0) Log.Warn($"reg {args} failed: {err.Trim()}");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error($"reg {args}", ex);
            return false;
        }
    }

    /// <summary>Last write time of a registry key (when the key or its values last changed).</summary>
    public static DateTime? LastWriteTime(RegistryKey key)
    {
        try
        {
            var rc = RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var ft);
            if (rc != 0) return null;
            return DateTime.FromFileTime(ft);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass,
        IntPtr lpReserved, IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues,
        IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    /// <summary>
    /// Converts an MSI product code GUID to the "packed" form used under HKCR\Installer\Products.
    /// </summary>
    public static string? PackGuid(string? productCode)
    {
        if (productCode is null || !Guid.TryParse(productCode, out var g)) return null;
        var s = g.ToString("N").ToUpperInvariant();
        static string Rev(string x) => new(x.Reverse().ToArray());
        var parts = new[] { s[..8], s[8..12], s[12..16] }.Select(Rev);
        var tail = s[16..];
        var swapped = string.Concat(Enumerable.Range(0, tail.Length / 2).Select(i => $"{tail[i * 2 + 1]}{tail[i * 2]}"));
        return string.Concat(parts) + swapped;
    }
}
