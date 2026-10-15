using System.Runtime.InteropServices;
using System.Text;

namespace WinAppClear.Core.Win;

/// <summary>Win32 P/Invoke declarations shared across the core library.</summary>
internal static partial class Native
{
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint MovefileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileEx(string existing, string? newName, uint flags);

    // Job objects — used to wait for an uninstaller and every child process it spawns.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JobBasicAccounting info,
        int length, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct JobBasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    // Shell file operations — used to send files to the Recycle Bin.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    public const uint FoDelete = 0x0003;
    public const ushort FofSilent = 0x0004;
    public const ushort FofNoConfirmation = 0x0010;
    public const ushort FofAllowUndo = 0x0040;
    public const ushort FofNoErrorUi = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref ShFileOpStruct op);

    // Restart Manager — find processes that hold a file open.
    [StructLayout(LayoutKind.Sequential)]
    public struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmRegisterResources(uint session, uint nFiles, string[] files, uint nApps,
        RmUniqueProcess[]? apps, uint nServices, string[]? services);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info,
        ref uint rebootReasons);

    // System Restore.
    // srrestoreptapi.h declares these with #pragma pack(1).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    public struct RestorePointInfo
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StateMgrStatus
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    public static extern bool SRSetRestorePointW(ref RestorePointInfo info, out StateMgrStatus status);

    // Window → process (Easy Uninstall crosshair).
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point p);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point p);
}
