namespace WinAppClear.Core.Util;

/// <summary>Minimal thread-safe file logger: one file per day under the app's Logs folder.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            var file = Path.Combine(AppPaths.Logs, $"winappclear-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
            {
                File.AppendAllText(file, line);
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
