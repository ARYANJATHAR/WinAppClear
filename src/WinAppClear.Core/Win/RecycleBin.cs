using WinAppClear.Core.Util;

namespace WinAppClear.Core.Win;

/// <summary>Sends files and folders to the Recycle Bin (silently, no UI).</summary>
public static class RecycleBin
{
    public static bool Send(string path)
    {
        try
        {
            var op = new Native.ShFileOpStruct
            {
                wFunc = Native.FoDelete,
                pFrom = path + "\0\0",
                fFlags = (ushort)(Native.FofAllowUndo | Native.FofNoConfirmation | Native.FofSilent | Native.FofNoErrorUi),
            };
            var rc = Native.SHFileOperation(ref op);
            if (rc != 0) Log.Warn($"Recycle {path} failed with code {rc}");
            return rc == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            Log.Warn($"Recycle {path}: {ex.Message}");
            return false;
        }
    }
}
