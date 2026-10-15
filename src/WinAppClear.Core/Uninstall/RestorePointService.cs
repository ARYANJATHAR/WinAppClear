using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Uninstall;

/// <summary>Creates a System Restore point before risky changes (requires admin and System Protection on).</summary>
public static class RestorePointService
{
    private const int BeginSystemChange = 100;
    private const int EndSystemChange = 101;
    private const int ApplicationUninstall = 1;

    public sealed record Result(bool Success, string Message);

    public static Task<Result> CreateAsync(string description) => Task.Run(() => Create(description));

    public static Result Create(string description)
    {
        try
        {
            var info = new Native.RestorePointInfo
            {
                dwEventType = BeginSystemChange,
                dwRestorePtType = ApplicationUninstall,
                llSequenceNumber = 0,
                szDescription = description.Length > 250 ? description[..250] : description,
            };
            if (!Native.SRSetRestorePointW(ref info, out var status))
            {
                var msg = status.nStatus switch
                {
                    1058 => "System Restore is turned off on this PC.",
                    5 => "Administrator rights are needed to create a restore point.",
                    _ => $"Windows could not create a restore point (code {status.nStatus}).",
                };
                Log.Warn($"Restore point failed: {status.nStatus}");
                return new Result(false, msg);
            }

            var end = new Native.RestorePointInfo
            {
                dwEventType = EndSystemChange,
                dwRestorePtType = ApplicationUninstall,
                llSequenceNumber = status.llSequenceNumber,
                szDescription = "",
            };
            Native.SRSetRestorePointW(ref end, out _);
            Log.Info($"Restore point created: {description} (#{status.llSequenceNumber})");
            return new Result(true, "Restore point created.");
        }
        catch (DllNotFoundException)
        {
            return new Result(false, "System Restore is not available on this edition of Windows.");
        }
        catch (Exception ex)
        {
            Log.Error("Restore point", ex);
            return new Result(false, ex.Message);
        }
    }
}
