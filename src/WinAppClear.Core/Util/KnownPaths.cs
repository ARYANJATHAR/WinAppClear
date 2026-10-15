using System.Runtime.InteropServices;

namespace WinAppClear.Core.Util;

/// <summary>Well-known Windows folders used by scanners and the safety guard.</summary>
public static class KnownPaths
{
    private static string Sf(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    public static string Windows => Sf(Environment.SpecialFolder.Windows);
    public static string System32 => Sf(Environment.SpecialFolder.System);
    public static string ProgramFiles => Sf(Environment.SpecialFolder.ProgramFiles);
    public static string ProgramFilesX86 => Sf(Environment.SpecialFolder.ProgramFilesX86);
    public static string CommonProgramFiles => Sf(Environment.SpecialFolder.CommonProgramFiles);
    public static string CommonProgramFilesX86 => Sf(Environment.SpecialFolder.CommonProgramFilesX86);
    public static string ProgramData => Sf(Environment.SpecialFolder.CommonApplicationData);
    public static string AppData => Sf(Environment.SpecialFolder.ApplicationData);
    public static string LocalAppData => Sf(Environment.SpecialFolder.LocalApplicationData);
    public static string LocalLow => Path.Combine(UserProfile, "AppData", "LocalLow");
    public static string UserProfile => Sf(Environment.SpecialFolder.UserProfile);
    public static string StartMenuPrograms => Sf(Environment.SpecialFolder.Programs);
    public static string CommonStartMenuPrograms => Sf(Environment.SpecialFolder.CommonPrograms);
    public static string Desktop => Sf(Environment.SpecialFolder.DesktopDirectory);
    public static string CommonDesktop => Sf(Environment.SpecialFolder.CommonDesktopDirectory);
    public static string Startup => Sf(Environment.SpecialFolder.Startup);
    public static string CommonStartup => Sf(Environment.SpecialFolder.CommonStartup);
    public static string Documents => Sf(Environment.SpecialFolder.MyDocuments);
    public static string Temp => Path.GetTempPath();
    public static string Prefetch => Path.Combine(Windows, "Prefetch");
    public static string SystemTasks => Path.Combine(System32, "Tasks");

    public static string Downloads
    {
        get
        {
            var downloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(downloadsId, 0, IntPtr.Zero, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p)!; }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            return Path.Combine(UserProfile, "Downloads");
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags,
        IntPtr token, out IntPtr path);
}
