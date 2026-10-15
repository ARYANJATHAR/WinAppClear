using System.Security.Cryptography;
using WinAppClear.Core.Safety;
using WinAppClear.Core.Util;

namespace WinAppClear.Core.Cleanup;

public sealed record ShredResult(string Path, bool Success, string? Message);

/// <summary>
/// Overwrites files with random data before deleting them so they can't be undeleted.
/// Note: on SSDs, wear-levelling means old blocks may survive — full-disk encryption is the real fix there.
/// </summary>
public static class FileShredder
{
    public static List<ShredResult> Shred(IEnumerable<string> paths, int passes, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        passes = Math.Clamp(passes, 1, 7);
        var results = new List<ShredResult>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var refusal = SafetyGuard.ProtectedPathReason(path);
            if (refusal is not null)
            {
                results.Add(new(path, false, $"Protected: {refusal}"));
                continue;
            }

            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Shredding {file}");
                    results.Add(ShredFile(file, passes));
                }
                try
                {
                    Directory.Delete(path, recursive: true);
                }
                catch (Exception ex)
                {
                    results.Add(new(path, false, $"Folder not removed: {ex.Message}"));
                }
            }
            else if (File.Exists(path))
            {
                progress?.Report($"Shredding {path}");
                results.Add(ShredFile(path, passes));
            }
            else
            {
                results.Add(new(path, false, "Not found"));
            }
        }
        return results;
    }

    public static ShredResult ShredFile(string file, int passes)
    {
        try
        {
            File.SetAttributes(file, FileAttributes.Normal);
            var length = new FileInfo(file).Length;
            var buffer = new byte[Math.Min(1024 * 1024, Math.Max(length, 1))];
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                for (var pass = 0; pass < passes; pass++)
                {
                    fs.Position = 0;
                    long written = 0;
                    while (written < length)
                    {
                        var chunk = (int)Math.Min(buffer.Length, length - written);
                        if (pass == passes - 1 && passes > 1) Array.Clear(buffer, 0, chunk); // final zero pass
                        else RandomNumberGenerator.Fill(buffer.AsSpan(0, chunk));
                        fs.Write(buffer, 0, chunk);
                        written += chunk;
                    }
                    fs.Flush(flushToDisk: true);
                }
                fs.SetLength(0);
            }

            // Rename to a random name so the original file name doesn't linger in the file table.
            var renamed = Path.Combine(Path.GetDirectoryName(file)!, Path.GetRandomFileName());
            File.Move(file, renamed);
            File.Delete(renamed);
            return new(file, true, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Shred {file}: {ex.Message}");
            return new(file, false, ex.Message);
        }
    }
}
