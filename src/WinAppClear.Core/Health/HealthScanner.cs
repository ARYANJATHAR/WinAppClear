using WinAppClear.Core.Util;

namespace WinAppClear.Core.Health;

public sealed record HealthCheckResult(IHealthCheck Check, List<HealthIssue> Issues, string? Error);

/// <summary>Runs all nine Software Health checks.</summary>
public static class HealthScanner
{
    public static IReadOnlyList<IHealthCheck> AllChecks { get; } =
    [
        new LeftoversCheck(),
        new OutdatedSoftwareCheck(),
        new PermissionsCheck(),
        new NotificationsCheck(),
        new UninstallIssuesCheck(),
        new InstallationFilesCheck(),
        new MaliciousCheck(),
        new RedundantFilesCheck(),
        new HibernationCheck(),
    ];

    public static async Task<List<HealthCheckResult>> ScanAllAsync(HealthContext context,
        IProgress<(IHealthCheck Check, HealthCheckResult? Result)>? progress = null, CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();
        foreach (var check in AllChecks)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((check, null));
            HealthCheckResult result;
            try
            {
                result = new HealthCheckResult(check, await check.ScanAsync(context, ct), null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"Health check {check.Title}", ex);
                result = new HealthCheckResult(check, [], ex.Message);
            }
            results.Add(result);
            progress?.Report((check, result));
        }
        Log.Info($"Software Health scan: {results.Sum(r => r.Issues.Count)} issues");
        return results;
    }
}
