using System.Diagnostics;
using System.Text;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Win;

/// <summary>Runs small helper commands (sc.exe, schtasks.exe, winget, adb) and captures their output.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
    }

    public static Result Run(string fileName, string arguments, int timeoutMs = 120_000, Encoding? encoding = null)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding ?? Encoding.UTF8,
                StandardErrorEncoding = encoding ?? Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new Result(-1, stdout.IsCompleted ? stdout.Result : "", "Timed out");
            }
            p.WaitForExit();
            return new Result(p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex)
        {
            Log.Warn($"Run {fileName} {arguments} failed: {ex.Message}");
            return new Result(-1, "", ex.Message);
        }
    }

    public static async Task<Result> RunAsync(string fileName, string arguments, CancellationToken ct = default,
        Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw;
            }
            return new Result(p.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Run {fileName} {arguments} failed: {ex.Message}");
            return new Result(-1, "", ex.Message);
        }
    }

    public static string SystemTool(string exe) => Path.Combine(KnownPaths.System32, exe);
}
