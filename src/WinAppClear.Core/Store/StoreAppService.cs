using WinAppClear.Core.Util;
using Windows.Management.Deployment;

namespace WinAppClear.Core.Store;

public sealed record StoreApp(
    string FullName,
    string FamilyName,
    string Name,
    string DisplayName,
    string Publisher,
    string Version,
    DateTime? InstalledDate,
    string? InstallLocation,
    string? LogoPath,
    bool IsRemovable)
{
    public string DataFolder => Path.Combine(KnownPaths.LocalAppData, "Packages", FamilyName);
}

/// <summary>Lists and removes Microsoft Store / MSIX apps for the current user.</summary>
public static class StoreAppService
{
    // Inbox apps that Windows needs or that can't be removed per-user.
    private static readonly string[] CriticalPrefixes =
    [
        "Microsoft.Windows.", "Microsoft.UI.", "Microsoft.VCLibs", "Microsoft.NET.", "Microsoft.Services.Store",
        "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI", "Microsoft.AAD.",
        "Microsoft.AccountsControl", "Microsoft.LockApp", "Microsoft.ECApp", "Microsoft.CredDialogHost",
        "Microsoft.Win32WebViewHost", "Microsoft.WindowsAppRuntime", "MicrosoftWindows.", "windows.",
        "Microsoft.StorePurchaseApp", "Microsoft.WebExperience", "Microsoft.WindowsTerminal", "Microsoft.Xbox.TCUI",
        "Microsoft.MicrosoftEdge", "Microsoft.Advertising", "Microsoft.Winget",
    ];

    public static List<StoreApp> GetApps()
    {
        var list = new List<StoreApp>();
        try
        {
            var pm = new PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (pkg.IsFramework || pkg.IsResourcePackage || pkg.IsBundle) continue;
                    if (pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                    var id = pkg.Id;
                    string? location = null, logo = null;
                    try { location = pkg.InstalledLocation?.Path; } catch { /* staged */ }
                    try { logo = pkg.Logo?.LocalPath; } catch { /* optional */ }
                    var display = SafeGet(() => pkg.DisplayName) ?? id.Name;
                    if (string.IsNullOrWhiteSpace(display) || display.StartsWith("ms-resource:", StringComparison.Ordinal))
                        display = id.Name;
                    var v = id.Version;
                    list.Add(new StoreApp(
                        id.FullName, id.FamilyName, id.Name, display,
                        SafeGet(() => pkg.PublisherDisplayName) ?? id.Publisher,
                        $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                        SafeGet<DateTime?>(() => pkg.InstalledDate.LocalDateTime),
                        location, logo,
                        !CriticalPrefixes.Any(p => id.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Store package read failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Store app enumeration failed", ex);
        }
        return list.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static async Task<(bool Success, string Message)> RemoveAsync(StoreApp app, CancellationToken ct = default)
    {
        if (!app.IsRemovable) return (false, "This app is part of Windows and is protected.");
        try
        {
            var pm = new PackageManager();
            var op = pm.RemovePackageAsync(app.FullName);
            var result = await op.AsTask(ct);
            if (result.ExtendedErrorCode is not null)
                return (false, result.ErrorText ?? result.ExtendedErrorCode.Message);
            Log.Info($"Removed store app {app.FullName}");
            return (true, "Removed.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Remove store app {app.FullName}: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private static T? SafeGet<T>(Func<T> f)
    {
        try { return f(); } catch { return default; }
    }
}
