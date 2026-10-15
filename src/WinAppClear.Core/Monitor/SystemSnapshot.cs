using System.IO.Compression;
using WinAppClear.Core.Util;
using Microsoft.Win32;

namespace WinAppClear.Core.Monitor;

/// <summary>
/// A point-in-time record of the file system areas and registry hives programs install into.
/// Comparing a snapshot taken before an install with one taken after shows exactly what the installer added.
/// </summary>
public sealed class SystemSnapshot
{
    public DateTime Taken { get; init; } = DateTime.Now;

    /// <summary>File path → (size, last write ticks).</summary>
    public Dictionary<string, (long Size, long Ticks)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"view|HKLM\path" for every key.</summary>
    public HashSet<string> RegistryKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"view|HKLM\path\0valueName" → hash of kind + data.</summary>
    public Dictionary<string, ulong> RegistryValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> FileRoots =>
    [
        KnownPaths.ProgramFiles, KnownPaths.ProgramFilesX86, KnownPaths.ProgramData, KnownPaths.AppData,
        KnownPaths.LocalAppData, KnownPaths.LocalLow, KnownPaths.Desktop, KnownPaths.CommonDesktop,
        KnownPaths.SystemTasks, Path.Combine(KnownPaths.System32, "drivers"),
    ];

    private static IReadOnlyList<string> ExcludedFolders =>
    [
        Path.Combine(KnownPaths.LocalAppData, "Temp"),
        Path.Combine(KnownPaths.LocalAppData, "Packages"),
        Path.Combine(KnownPaths.LocalAppData, "D3DSCache"),
        Path.Combine(KnownPaths.LocalAppData, "CrashDumps"),
        Path.Combine(KnownPaths.LocalAppData, @"Microsoft\Windows"),
        Path.Combine(KnownPaths.LocalAppData, @"Microsoft\Edge\User Data"),
        Path.Combine(KnownPaths.LocalAppData, @"Google\Chrome\User Data"),
        Path.Combine(KnownPaths.LocalAppData, @"BraveSoftware\Brave-Browser\User Data"),
        Path.Combine(KnownPaths.LocalAppData, @"Microsoft\OneDrive"),
        Path.Combine(KnownPaths.AppData, @"Mozilla\Firefox\Profiles"),
        Path.Combine(KnownPaths.AppData, @"Microsoft\Windows\Recent"),
        Path.Combine(KnownPaths.ProgramData, @"Microsoft\Windows Defender"),
        Path.Combine(KnownPaths.ProgramData, @"Microsoft\Search"),
        Path.Combine(KnownPaths.ProgramData, @"Microsoft\Diagnosis"),
        Path.Combine(KnownPaths.ProgramData, @"Microsoft\Windows\WER"),
        Path.Combine(KnownPaths.ProgramData, "Packages"),
        Path.Combine(KnownPaths.ProgramFiles, "WindowsApps"),
        AppPaths.Root,
    ];

    private static readonly string[] ExcludedFolderNames = ["Cache", "Code Cache", "GPUCache", "DawnCache", "ShaderCache", "INetCache"];

    public static IReadOnlyList<(RegistryHive Hive, RegistryView View, string SubKey)> RegistryRoots =>
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, "Software"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services"),
    ];

    private static readonly string[] ExcludedRegistryPaths =
    [
        @"SOFTWARE\WOW6432Node",
        @"SOFTWARE\Microsoft\Windows Defender",
        @"SOFTWARE\Microsoft\Windows Search",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Prefetcher",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Diagnostics",
        @"SOFTWARE\Microsoft\SystemCertificates",
        @"SOFTWARE\Microsoft\Cryptography",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CloudStore",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
        @"SOFTWARE\Microsoft\Windows\Shell",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags",
        @"SOFTWARE\Classes\Local Settings",
        @"SOFTWARE\Microsoft\Office\16.0\Common\Roaming",
        @"SOFTWARE\Microsoft\IdentityCRL",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Store",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Holographic",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\UFH",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies",
        @"SOFTWARE\Microsoft\Input",
        @"SOFTWARE\Microsoft\InputPersonalization",
        @"SOFTWARE\Microsoft\Speech_OneCore",
    ];

    /// <summary>Takes a snapshot. Takes from a few seconds to a couple of minutes depending on the PC.</summary>
    public static SystemSnapshot Take(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var snap = new SystemSnapshot();
        var excluded = ExcludedFolders.Select(PathUtil.TrimEnd).ToList();

        foreach (var root in FileRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            progress?.Report($"Recording files in {root}…");
            WalkFiles(snap, root, excluded, ct, recurse: !root.EndsWith("drivers", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var (hive, view, sub) in RegistryRoots)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Recording registry {RegistryPath.HiveShortName(hive)}\\{sub} ({RegistryPath.ViewName(view)}-bit)…");
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(sub);
                if (key is not null) WalkRegistry(snap, key, hive, RegistryPath.ViewName(view), sub, ct);
            }
            catch (Exception ex)
            {
                Log.Warn($"Snapshot registry {sub}: {ex.Message}");
            }
        }
        Log.Info($"Snapshot: {snap.Files.Count} files, {snap.Directories.Count} folders, {snap.RegistryKeys.Count} keys, {snap.RegistryValues.Count} values");
        return snap;
    }

    private static void WalkFiles(SystemSnapshot snap, string root, List<string> excluded, CancellationToken ct, bool recurse)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(dir);
                foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions
                         {
                             IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
                {
                    if (entry is FileInfo f)
                    {
                        snap.Files[f.FullName] = (f.Length, f.LastWriteTimeUtc.Ticks);
                    }
                    else if (entry is DirectoryInfo d)
                    {
                        if (ExcludedFolderNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                        if (excluded.Any(x => string.Equals(x, d.FullName, StringComparison.OrdinalIgnoreCase))) continue;
                        snap.Directories.Add(d.FullName);
                        if (recurse) stack.Push(d.FullName);
                    }
                }
            }
            catch
            {
                // Access denied etc.
            }
        }
    }

    private static void WalkRegistry(SystemSnapshot snap, RegistryKey key, RegistryHive hive, string view, string path, CancellationToken ct)
    {
        // Paths compare case-insensitively, so "SOFTWARE\..." exclusions also cover HKCU's "Software\...".
        if (ExcludedRegistryPaths.Any(x => path.Equals(x, StringComparison.OrdinalIgnoreCase))) return;

        var full = RegistryPath.Combine(hive, path);
        snap.RegistryKeys.Add($"{view}|{full}");
        try
        {
            foreach (var name in key.GetValueNames())
            {
                try
                {
                    var kind = key.GetValueKind(name);
                    var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    snap.RegistryValues[$"{view}|{full}\0{name}"] = Hash(kind, data);
                }
                catch
                {
                    // Unreadable value.
                }
            }

            foreach (var child in key.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var sub = key.OpenSubKey(child);
                    if (sub is not null) WalkRegistry(snap, sub, hive, view, $@"{path}\{child}", ct);
                }
                catch
                {
                    // Access denied.
                }
            }
        }
        catch
        {
            // Access denied.
        }
    }

    internal static ulong Hash(RegistryValueKind kind, object? data)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        var h = offset;
        void Mix(byte b)
        {
            h ^= b;
            h *= prime;
        }
        Mix((byte)kind);
        byte[] bytes = data switch
        {
            null => [],
            byte[] b => b,
            string s => System.Text.Encoding.UTF8.GetBytes(s),
            string[] a => System.Text.Encoding.UTF8.GetBytes(string.Join('\0', a)),
            int i => BitConverter.GetBytes(i),
            long l => BitConverter.GetBytes(l),
            _ => System.Text.Encoding.UTF8.GetBytes(data.ToString() ?? ""),
        };
        foreach (var b in bytes) Mix(b);
        return h;
    }

    // ---- Persistence (compressed binary) so a monitor survives closing the app. ----

    public void Save(string file)
    {
        using var fs = File.Create(file);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var w = new BinaryWriter(gz);
        w.Write(1); // format version
        w.Write(Taken.ToBinary());
        w.Write(Files.Count);
        foreach (var (k, v) in Files)
        {
            w.Write(k);
            w.Write(v.Size);
            w.Write(v.Ticks);
        }
        w.Write(Directories.Count);
        foreach (var d in Directories) w.Write(d);
        w.Write(RegistryKeys.Count);
        foreach (var k in RegistryKeys) w.Write(k);
        w.Write(RegistryValues.Count);
        foreach (var (k, v) in RegistryValues)
        {
            w.Write(k);
            w.Write(v);
        }
    }

    public static SystemSnapshot Load(string file)
    {
        using var fs = File.OpenRead(file);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz);
        if (r.ReadInt32() != 1) throw new InvalidDataException("Unknown snapshot format");
        var snap = new SystemSnapshot { Taken = DateTime.FromBinary(r.ReadInt64()) };
        var n = r.ReadInt32();
        for (var i = 0; i < n; i++) snap.Files[r.ReadString()] = (r.ReadInt64(), r.ReadInt64());
        n = r.ReadInt32();
        for (var i = 0; i < n; i++) snap.Directories.Add(r.ReadString());
        n = r.ReadInt32();
        for (var i = 0; i < n; i++) snap.RegistryKeys.Add(r.ReadString());
        n = r.ReadInt32();
        for (var i = 0; i < n; i++) snap.RegistryValues[r.ReadString()] = r.ReadUInt64();
        return snap;
    }
}
