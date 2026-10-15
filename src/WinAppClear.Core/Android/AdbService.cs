using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Android;

public sealed record AndroidDevice(string Serial, string State, string? Model);

public sealed record AndroidApp(string Package, string? ApkPath, bool IsSystem);

/// <summary>Lists and uninstalls apps on a USB-connected Android phone using adb (USB debugging must be on).</summary>
public static class AdbService
{
    public static string? FindAdb()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Trim().Length > 0) candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));
        }
        foreach (var env in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var root = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "platform-tools", "adb.exe"));
        }
        candidates.Add(Path.Combine(KnownPaths.LocalAppData, @"Android\Sdk\platform-tools\adb.exe"));
        candidates.Add(@"C:\Android\sdk\platform-tools\adb.exe");
        candidates.Add(@"C:\platform-tools\adb.exe");
        return candidates.FirstOrDefault(c =>
        {
            try { return File.Exists(c); } catch { return false; }
        });
    }

    public static async Task<(List<AndroidDevice> Devices, string? Error)> GetDevicesAsync(CancellationToken ct = default)
    {
        var adb = FindAdb();
        if (adb is null) return ([], "adb was not found. Install Android Platform Tools and add it to PATH.");
        var r = await ProcessRunner.RunAsync(adb, "devices -l", ct);
        if (!r.Ok) return ([], r.Error.Trim());
        var list = new List<AndroidDevice>();
        foreach (var line in r.Output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ');
            list.Add(new AndroidDevice(parts[0], parts[1], model));
        }
        return (list, null);
    }

    public static async Task<List<AndroidApp>> GetAppsAsync(string serial, bool includeSystem, CancellationToken ct = default)
    {
        var adb = FindAdb();
        if (adb is null) return [];
        var user = await ProcessRunner.RunAsync(adb, $"-s {serial} shell pm list packages -3 -f", ct);
        var list = ParsePackages(user.Output, isSystem: false);
        if (includeSystem)
        {
            var sys = await ProcessRunner.RunAsync(adb, $"-s {serial} shell pm list packages -s -f", ct);
            list.AddRange(ParsePackages(sys.Output, isSystem: true));
        }
        return list.OrderBy(a => a.IsSystem).ThenBy(a => a.Package).ToList();
    }

    internal static List<AndroidApp> ParsePackages(string output, bool isSystem)
    {
        var list = new List<AndroidApp>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;
            line = line[8..];
            var eq = line.LastIndexOf('=');
            if (eq < 0) list.Add(new AndroidApp(line, null, isSystem));
            else list.Add(new AndroidApp(line[(eq + 1)..], line[..eq], isSystem));
        }
        return list;
    }

    /// <summary>
    /// Uninstalls an app. System apps are removed for the current user only (pm uninstall --user 0),
    /// which is reversible with "adb shell cmd package install-existing &lt;package&gt;".
    /// </summary>
    public static async Task<(bool Success, string Message)> UninstallAsync(string serial, AndroidApp app, CancellationToken ct = default)
    {
        var adb = FindAdb();
        if (adb is null) return (false, "adb not found.");
        var args = app.IsSystem ? $"-s {serial} shell pm uninstall -k --user 0 {app.Package}" : $"-s {serial} uninstall {app.Package}";
        var r = await ProcessRunner.RunAsync(adb, args, ct);
        var text = (r.Output + r.Error).Trim();
        var ok = text.Contains("Success", StringComparison.OrdinalIgnoreCase);
        Log.Info($"adb uninstall {app.Package}: {text}");
        return (ok, ok ? "Uninstalled." : text);
    }
}
