using System.Diagnostics;
using WinAppClear.Core.Inventory;
using WinAppClear.Core.Models;
using WinAppClear.Core.Util;
using WinAppClear.Core.Win;

namespace WinAppClear.Core.Uninstall;

public enum UninstallOutcome
{
    Removed,
    StillInstalled,
    NoUninstaller,
    Failed,
    Cancelled,
}

public sealed record UninstallResult(UninstallOutcome Outcome, int? ExitCode, string Message, bool UsedQuietMode);

/// <summary>
/// Runs a program's own uninstaller and waits for it — including any child processes it spawns
/// (Inno Setup and NSIS uninstallers copy themselves to %TEMP% and re-launch).
/// </summary>
public static class UninstallRunner
{
    public static async Task<UninstallResult> RunAsync(InstalledProgram program, bool quiet,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var command = quiet ? UninstallCommand.Quiet(program) ?? UninstallCommand.Normal(program) : UninstallCommand.Normal(program);
        var usedQuiet = quiet && UninstallCommand.Quiet(program) is not null;
        if (command is null)
            return new UninstallResult(UninstallOutcome.NoUninstaller, null, "This program has no uninstaller registered.", false);

        if (!UninstallCommand.IsMsiExec(command.FileName))
        {
            var resolved = UninstallCommand.ResolveExecutable(command.FileName);
            if (resolved is null)
                return new UninstallResult(UninstallOutcome.NoUninstaller, null,
                    $"The uninstaller is missing: {command.FileName}. Use Force Uninstall instead.", false);
            command = command with { FileName = resolved };
        }

        progress?.Report($"Running {Path.GetFileName(command.FileName)}…");
        Log.Info($"Uninstall '{program.DisplayName}': {command}");

        var exit = await RunAndWaitForTreeAsync(command, ct);
        if (exit is null)
            return new UninstallResult(UninstallOutcome.Cancelled, null, "Uninstall was cancelled.", usedQuiet);

        // Give the registry a moment — some uninstallers remove their key last.
        for (var i = 0; i < 10 && ProgramInventory.StillInstalled(program); i++) await Task.Delay(500, ct);

        var still = ProgramInventory.StillInstalled(program);
        // 1605 = MSI "unknown product" (already gone), 3010/1641 = success, reboot required.
        var exitCode = exit.Value;
        var outcome = !still ? UninstallOutcome.Removed
            : exitCode is 1602 or 1223 ? UninstallOutcome.Cancelled
            : UninstallOutcome.StillInstalled;
        var message = outcome switch
        {
            UninstallOutcome.Removed when exitCode is 3010 or 1641 => "Uninstalled. Windows needs a restart to finish.",
            UninstallOutcome.Removed => "Uninstalled.",
            UninstallOutcome.Cancelled => "The uninstaller was cancelled.",
            _ => $"The program still appears to be installed (exit code {exitCode}).",
        };
        Log.Info($"Uninstall '{program.DisplayName}' finished: {outcome}, exit {exitCode}");
        return new UninstallResult(outcome, exitCode, message, usedQuiet);
    }

    /// <summary>Starts a process inside a job object and waits until every process in the job has exited.</summary>
    public static async Task<int?> RunAndWaitForTreeAsync(CommandLine command, CancellationToken ct, string? workingDirectory = null)
    {
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        try
        {
            var psi = new ProcessStartInfo(command.FileName, command.Arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ??
                                   (Path.GetDirectoryName(command.FileName) is { Length: > 0 } d && Directory.Exists(d) ? d : KnownPaths.Temp),
            };
            using var process = Process.Start(psi);
            if (process is null) return -1;
            if (job != IntPtr.Zero) Native.AssignProcessToJobObject(job, process.Handle);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            var exitCode = process.ExitCode;

            // Wait for children that inherited the job (e.g. the real uninstaller copied to %TEMP%).
            if (job != IntPtr.Zero)
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!Native.QueryInformationJobObject(job, 1, out var info,
                            System.Runtime.InteropServices.Marshal.SizeOf<Native.JobBasicAccounting>(), IntPtr.Zero))
                        break;
                    if (info.ActiveProcesses == 0) break;
                    await Task.Delay(500, CancellationToken.None);
                }
                if (ct.IsCancellationRequested) return null;
            }
            return exitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223; // user declined UAC
        }
        finally
        {
            if (job != IntPtr.Zero) Native.CloseHandle(job);
        }
    }

    /// <summary>Starts the program's repair/modify command (no waiting needed beyond the main process).</summary>
    public static bool StartRepair(InstalledProgram program)
    {
        var cmd = UninstallCommand.Repair(program);
        if (cmd is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(cmd.FileName, cmd.Arguments) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Repair {program.DisplayName}: {ex.Message}");
            return false;
        }
    }
}
