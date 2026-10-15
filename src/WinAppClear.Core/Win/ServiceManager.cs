using System.ServiceProcess;
using WinAppClear.Core.Util;
using Microsoft.Win32;

namespace WinAppClear.Core.Win;

public enum ServiceStartMode2
{
    Boot = 0,
    System = 1,
    Automatic = 2,
    Manual = 3,
    Disabled = 4,
}

public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    string? ImagePath,
    string? BinaryPath,
    ServiceStartMode2 StartMode,
    bool IsDriver,
    bool DelayedAutoStart)
{
    public bool IsAutoStart => StartMode is ServiceStartMode2.Automatic or ServiceStartMode2.Boot or ServiceStartMode2.System;
}

/// <summary>Lists Windows services from the registry and changes them through sc.exe.</summary>
public static class ServiceManager
{
    public const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public static List<ServiceInfo> GetServices(bool includeDrivers = false)
    {
        var list = new List<ServiceInfo>();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(ServicesKey);
        if (root is null) return list;
        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(name);
                if (key is null) continue;
                var type = key.GetValue("Type") is int t ? t : 0;
                var isDriver = (type & 0x3) != 0;
                var isWin32 = (type & 0x30) != 0;
                if (!isWin32 && !(includeDrivers && isDriver)) continue;
                var imagePath = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var start = key.GetValue("Start") is int s ? (ServiceStartMode2)s : ServiceStartMode2.Manual;
                var display = key.GetValue("DisplayName") as string ?? name;
                if (display.StartsWith('@')) display = name; // resource string — not worth resolving
                list.Add(new ServiceInfo(name, display, imagePath, ResolveBinary(imagePath), start, isDriver,
                    key.GetValue("DelayedAutostart") is int d && d == 1));
            }
            catch
            {
                // Unreadable service key — skip.
            }
        }
        return list;
    }

    /// <summary>Turns an ImagePath like "\"C:\x\svc.exe\" -k" or "\SystemRoot\..." into a full binary path.</summary>
    public static string? ResolveBinary(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var p = imagePath.Trim();
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            p = KnownPaths.Windows + p[11..];
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(KnownPaths.Windows, p);
        else if (p.StartsWith(@"\??\", StringComparison.Ordinal))
            p = p[4..];
        return PathUtil.ExtractExecutablePath(p);
    }

    public static bool IsRunning(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    public static bool Stop(string name, TimeSpan? timeout = null)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status == ServiceControllerStatus.Stopped) return true;
            if (sc.CanStop) sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout ?? TimeSpan.FromSeconds(20));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Stop service {name}: {ex.Message}");
            return false;
        }
    }

    public static bool Start(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status == ServiceControllerStatus.Running) return true;
            sc.Start();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Start service {name}: {ex.Message}");
            return false;
        }
    }

    public static bool SetStartMode(string name, ServiceStartMode2 mode, bool delayed = false)
    {
        var start = mode switch
        {
            ServiceStartMode2.Automatic => delayed ? "delayed-auto" : "auto",
            ServiceStartMode2.Manual => "demand",
            ServiceStartMode2.Disabled => "disabled",
            ServiceStartMode2.Boot => "boot",
            ServiceStartMode2.System => "system",
            _ => "demand",
        };
        var r = ProcessRunner.Run(ProcessRunner.SystemTool("sc.exe"), $"config \"{name}\" start= {start}");
        if (!r.Ok) Log.Warn($"sc config {name} start= {start}: {r.Output.Trim()} {r.Error.Trim()}");
        return r.Ok;
    }

    public static bool Delete(string name)
    {
        Stop(name);
        var r = ProcessRunner.Run(ProcessRunner.SystemTool("sc.exe"), $"delete \"{name}\"");
        if (!r.Ok) Log.Warn($"sc delete {name}: {r.Output.Trim()} {r.Error.Trim()}");
        return r.Ok;
    }

    public static bool Exists(string name)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey($@"{ServicesKey}\{name}");
        return key is not null;
    }
}
