using System.IO;

namespace SideLoadr.Services;

/// <summary>Shared re-sign + reinstall loop for everything expiring soon.
/// Used by the GUI button, the headless CLI, and the scheduled task.</summary>
public static class RefreshAllService
{
    public static async Task<(int Renewed, int Total)> RunAsync(
        string appleId, string password, string anisette,
        Func<Task<string>> request2fa, Action<string> log, CancellationToken ct)
    {
        var expiring = AppTracker.Load()
            .Where(a => a.DaysLeft <= 2 && !string.IsNullOrEmpty(a.IpaPath))
            .ToList();
        int ok = 0;
        foreach (var app in expiring)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(app.IpaPath))
            {
                log($"SKIP {app.BundleName}: original IPA not found at {app.IpaPath}");
                continue;
            }
            try
            {
                var req = new SignRequest(app.IpaPath, appleId, password, anisette, app.Udid, request2fa);
                string signed = await SigningService.SignWithFreeAccountAsync(req, log, ct);
                await InstallService.InstallAsync(signed, string.IsNullOrEmpty(app.Udid) ? null : app.Udid, log, ct);
                AppTracker.RecordInstalled(app.BundleId, app.BundleName, app.Udid, app.IpaPath);
                ok++;
            }
            catch (Exception ex)
            {
                log($"FAILED {app.BundleName}: {ex.Message}");
            }
        }
        return (ok, expiring.Count);
    }
}
