using System.IO;

namespace SideLoadr.Services;

/// <summary>One refresh implementation for every caller: GUI button, headless
/// CLI/scheduled task, and the Spryzen helper endpoint. Three ways in:
/// (1) free Apple ID flow with saved credentials, (2) caller-provided
/// profile + cert + key, (3) caller-provided .p12 + profile. The IPA comes
/// from the tracker, or base64-attached when the caller has the file.</summary>
public static class HelperRefreshService
{
    public static async Task<(HelperResult Result, bool TrackerChanged)> HandleAsync(
        RefreshRequest req, Func<Task<string>> request2fa, Action<string> log, CancellationToken ct)
    {
        try
        {
            return await HandleInnerAsync(req, request2fa, log, ct);
        }
        catch (Exception ex)
        {
            return (new HelperResult(false, ex.Message), false);
        }
    }

    private static async Task<(HelperResult, bool)> HandleInnerAsync(
        RefreshRequest req, Func<Task<string>> request2fa, Action<string> log, CancellationToken ct)
    {
        string workDir = Path.Combine(Path.GetTempPath(), "SideLoadr", "helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        string? ipaPath = null;
        if (!string.IsNullOrEmpty(req.IpaB64))
        {
            ipaPath = Path.Combine(workDir, SafeFileName(req.Filename) ?? "upload.ipa");
            await File.WriteAllBytesAsync(ipaPath, Convert.FromBase64String(req.IpaB64), ct);
            log($"Received IPA ({new FileInfo(ipaPath).Length / 1024} KB).");
        }

        string? profilePath = null;
        string? keyPemPath = null;
        string? certPemPath = null;
        bool hasMaterials = !string.IsNullOrEmpty(req.Profile);
        if (hasMaterials)
        {
            profilePath = Path.Combine(workDir, "req.mobileprovision");
            await File.WriteAllBytesAsync(profilePath, Convert.FromBase64String(req.Profile!), ct);
            if (!string.IsNullOrEmpty(req.CertPem) && !string.IsNullOrEmpty(req.KeyPem))
            {
                certPemPath = Path.Combine(workDir, "req-cert.pem");
                keyPemPath = Path.Combine(workDir, "req-key.pem");
                await File.WriteAllTextAsync(certPemPath, req.CertPem, ct);
                await File.WriteAllTextAsync(keyPemPath, req.KeyPem, ct);
            }
            else if (!string.IsNullOrEmpty(req.P12B64))
            {
                byte[] pfx = Convert.FromBase64String(req.P12B64);
                (keyPemPath, certPemPath) = CustomCertService.ExtractPems(pfx, req.P12Password ?? "", workDir, log);
            }
            else
            {
                return (new HelperResult(false, "Profile needs a cert+key or a .p12 to go with it."), false);
            }
        }

        string? udid = string.IsNullOrEmpty(req.Udid) ? null : req.Udid;
        if (ipaPath is null)
        {
            var app = AppTracker.Load().FirstOrDefault(a =>
                a.BundleId == req.BundleId && (udid is null || a.Udid == udid));
            if (app is null || string.IsNullOrEmpty(app.IpaPath))
                return (new HelperResult(false, "Unknown app — install it from SideLoadr first."), false);
            ipaPath = app.IpaPath;
            if (string.IsNullOrEmpty(udid))
                udid = string.IsNullOrEmpty(app.Udid) ? null : app.Udid;
        }

        if (!File.Exists(ipaPath))
            return (new HelperResult(false, $"Original IPA not found at {ipaPath}."), false);

        if (hasMaterials)
        {
            string signed = await SigningService.ResignWithMaterialsAsync(
                ipaPath, profilePath!, keyPemPath!, certPemPath!, log, ct);
            await InstallService.InstallAsync(signed, udid, log, ct);
            var info = IpaReader.Read(ipaPath);
            AppTracker.RecordInstalled(info.BundleId, info.BundleName, udid, ipaPath);
            return (new HelperResult(true, $"Installed {info.BundleName} with the provided certificate."), true);
        }

        if (!CredentialStore.TryLoad(out string appleId, out string password))
            return (new HelperResult(false, "No saved credentials — turn on Remember me in SideLoadr."), false);
        string anisette = AppTracker.LoadSettings().AnisetteUrl;
        if (string.IsNullOrWhiteSpace(anisette))
            anisette = AppTracker.DefaultAnisetteUrl;
        var signReq = new SignRequest(ipaPath, appleId, password, anisette, udid, request2fa);
        string resigned = await SigningService.SignWithFreeAccountAsync(signReq, log, ct);
        await InstallService.InstallAsync(resigned, udid, log, ct);
        var done = IpaReader.Read(ipaPath);
        AppTracker.RecordInstalled(done.BundleId, done.BundleName, udid, ipaPath);
        return (new HelperResult(true, $"Renewed {done.BundleName}."), true);
    }

    private static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string clean = Path.GetFileName(name);
        if (clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        return clean.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase) ? clean : null;
    }
}
