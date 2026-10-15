using System.Text;
using System.Text.RegularExpressions;

namespace WinAppClear.Core.Util;

/// <summary>
/// Normalizes program, publisher and folder names so "Foo Bar 2.3 (x64)" and "FooBar" can be compared.
/// </summary>
public static partial class NameMatcher
{
    /// <summary>
    /// Folder and key names too generic to ever be treated as one program's leftovers.
    /// Compared against the normalized form.
    /// </summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "commonfiles", "common", "shared", "system", "system32", "programs",
        "program", "programfiles", "installer", "installshield", "installshieldinstallationinformation",
        "packagecache", "temp", "tmp", "cache", "caches", "data", "logs", "log", "config", "settings",
        "update", "updater", "updates", "app", "apps", "application", "applications", "software",
        "classes", "policies", "wow6432node", "clients", "google", "mozilla", "apple", "intel", "amd",
        "nvidia", "nvidiacorporation", "realtek", "dell", "hp", "lenovo", "asus", "oracle", "java",
        "adobe", "packages", "users", "user", "default", "public", "desktop", "documents", "downloads",
        "startmenu", "startup", "tools", "utilities", "drivers", "driver", "runtime", "runtimes",
        "dotnet", "net", "framework", "sdk", "msbuild", "referenceassemblies", "windowsapps",
        "windowspowershell", "internetexplorer", "windowsdefender", "windowsmail", "windowsmediaplayer",
        "windowsnt", "windowsphotoviewer", "windowssidebar", "modifiablewindowsapps", "msi", "setup",
        "crashdumps", "crashreports", "d3dscache", "fonts", "help", "vendor", "plugins", "extensions",
        "client", "service", "services", "launcher", "helper", "bin", "lib", "resources", "assets",
        "local", "roaming", "locallow", "appdata", "programdata", "comms", "connecteddevicesplatform",
        "virtualstore", "inetcache", "history", "steam", "epicgames", "games", "microsoftcorporation",
    };

    private static readonly string[] CompanySuffixes =
    [
        "incorporated", "inc", "corporation", "corp", "company", "co", "limited", "ltd", "llc", "gmbh", "ag",
        "sa", "srl", "bv", "pvt", "private", "plc", "oy", "ab", "as", "kk", "pty", "software", "technologies",
        "technology", "systems", "labs", "studio", "studios", "group", "holdings", "international",
    ];

    [GeneratedRegex(@"\(.*?\)|\[.*?\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\b(v(ersion)?\s*)?\d+([.\-_]\d+)*[a-z]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Versions();

    [GeneratedRegex(@"\b(x64|x86|64[\s-]?bit|32[\s-]?bit|amd64|arm64|win64|win32|en-us|portable|remove only)\b",RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    /// <summary>Lowercase letters and digits only, with versions/architecture tags removed.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = Brackets().Replace(name, " ");
        s = Noise().Replace(s, " ");
        s = Versions().Replace(s, " ");
        return AlphaNumeric(s);
    }

    /// <summary>Publisher name without legal suffixes ("Foo Software Inc." → "foo").</summary>
    public static string NormalizePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return "";
        var words = Words(publisher).ToList();
        while (words.Count > 1 && CompanySuffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Concat(words);
    }

    public static IEnumerable<string> Words(string value) =>
        Regex.Split(value.ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 0);

    public static string AlphaNumeric(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static bool IsGeneric(string? normalized) =>
        string.IsNullOrEmpty(normalized) || normalized.Length < 3 || GenericNames.Contains(normalized);

    /// <summary>
    /// Scores how well a folder/key name matches a program. 0 = no match, 2 = exact, 1 = strong prefix.
    /// </summary>
    public static int Score(string candidateName, IReadOnlyCollection<string> programKeys)
    {
        var c = Normalize(candidateName);
        if (IsGeneric(c)) return 0;
        var best = 0;
        foreach (var key in programKeys)
        {
            if (IsGeneric(key)) continue;
            if (c == key) return 2;
            // Prefix matches only count for reasonably distinctive names.
            var shorter = Math.Min(c.Length, key.Length);
            if (shorter >= 6 && (c.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(c, StringComparison.Ordinal)))
            {
                best = Math.Max(best, 1);
            }
        }
        return best;
    }
}
