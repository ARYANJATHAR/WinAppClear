namespace WinAppClear.Core.Util;

public static class PathUtil
{
    /// <summary>Expands env vars, strips quotes and trailing separators, returns a full path or null.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());
        if (p.Length == 0) return null;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(p)) return null;
            p = System.IO.Path.GetFullPath(p);
        }
        catch
        {
            return null;
        }
        return TrimEnd(p);
    }

    public static string TrimEnd(string path)
    {
        var root = System.IO.Path.GetPathRoot(path);
        if (root is not null && path.Length <= root.Length) return path;
        return path.TrimEnd('\\', '/');
    }

    /// <summary>True when <paramref name="path"/> equals or is inside <paramref name="folder"/>.</summary>
    public static bool IsUnderOrEqual(string? path, string? folder)
    {
        if (path is null || folder is null) return false;
        var p = TrimEnd(path);
        var f = TrimEnd(folder);
        if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = f.EndsWith('\\') || f.EndsWith('/') ? f : f + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStrictlyUnder(string? path, string? folder) =>
        path is not null && folder is not null &&
        !TrimEnd(path).Equals(TrimEnd(folder), StringComparison.OrdinalIgnoreCase) &&
        IsUnderOrEqual(path, folder);

    /// <summary>Number of path segments below the drive root ("C:\a\b" → 2).</summary>
    public static int Depth(string path)
    {
        var root = System.IO.Path.GetPathRoot(path) ?? "";
        var rest = path[root.Length..].Trim('\\');
        return rest.Length == 0 ? 0 : rest.Split('\\').Length;
    }

    /// <summary>Extracts an executable path from a value like "C:\x\app.exe,0" or "\"C:\x\app.exe\" -arg".</summary>
    public static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = Environment.ExpandEnvironmentVariables(value.Trim());
        if (v.StartsWith('"'))
        {
            var end = v.IndexOf('"', 1);
            if (end > 1) return Normalize(v[1..end]);
        }
        var comma = v.LastIndexOf(',');
        if (comma > 0 && int.TryParse(v[(comma + 1)..].Trim(), out _)) v = v[..comma];
        var exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return Normalize(v[..(exe + 4)]);
        var ico = v.IndexOf(".ico", StringComparison.OrdinalIgnoreCase);
        if (ico > 0) return Normalize(v[..(ico + 4)]);
        var dll = v.IndexOf(".dll", StringComparison.OrdinalIgnoreCase);
        if (dll > 0) return Normalize(v[..(dll + 4)]);
        return Normalize(v);
    }

    public static long DirectorySize(string path, CancellationToken ct = default)
    {
        long total = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                try { total += file.Length; } catch { /* skip */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* inaccessible root */ }
        return total;
    }

    public static string FormatSize(long? bytes)
    {
        if (bytes is null) return "";
        double b = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (b >= 1024 && i < units.Length - 1)
        {
            b /= 1024;
            i++;
        }
        return i == 0 ? $"{b:0} {units[i]}" : $"{b:0.#} {units[i]}";
    }
}
