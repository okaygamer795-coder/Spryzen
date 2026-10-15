using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SideLoadr.Services;

public sealed record SignRequest(
    string IpaPath,
    string AppleId,
    string Password,
    string AnisetteUrl,
    string? Udid,
    Func<Task<string>> Request2faCode);

/// <summary>Free-Apple-ID signing pipeline: login → App ID → device → cert →
/// profile → resign with zsign → resigned IPA. Free profiles last 7 days.</summary>
public static class SigningService
{
    public static async Task<string> SignWithFreeAccountAsync(SignRequest req, Action<string> log, CancellationToken ct)
    {
        var ipa = IpaReader.Read(req.IpaPath);
        log($"IPA: {ipa.BundleName} ({ipa.BundleId}) v{ipa.Version}");

        if (req.Udid is null)
            throw new InvalidOperationException("No device UDID — connect the iPhone and press Refresh first.");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Xcode");
        var auth = new AppleAuthService(http, req.AnisetteUrl);
        AppleSession session = await auth.LoginAsync(req.AppleId, req.Password, req.Request2faCode, log, ct);

        var portal = new DeveloperPortalService(http, session);
        await portal.EnsureAppIdAsync(ipa.BundleId, log, ct);
        await portal.EnsureDeviceAsync(req.Udid, "SideLoadr device", log, ct);

        using RSA rsa = RSA.Create(2048);
        byte[] csr = CreateCsr(rsa, req.AppleId);
        string certPem = await portal.FetchDevelopmentCertAsync(csr, log, ct);
        string profilePath = await portal.FetchProfileAsync(ipa.BundleId, log, ct);

        string keyPemPath = Path.Combine(Path.GetDirectoryName(profilePath)!, "key.pem");
        await File.WriteAllTextAsync(keyPemPath, ExportPrivateKeyPem(rsa), ct);
        string certPath = Path.Combine(Path.GetDirectoryName(profilePath)!, "cert.pem");
        await File.WriteAllTextAsync(certPath, EnsurePem(certPem, "CERTIFICATE"), ct);

        return await ResignAsync(req.IpaPath, profilePath, keyPemPath, certPath, log, ct);
    }

    private static byte[] CreateCsr(RSA rsa, string appleId)
    {
        var dn = new X500DistinguishedName($"CN=iPhone Developer, OU=SideLoadr, O=SideLoadr, C=US");
        var request = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        _ = appleId; // Apple ignores the subject; kept for future CSR attributes.
        return request.CreateSigningRequest();
    }

    private static string ExportPrivateKeyPem(RSA rsa)
    {
        string b64 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        return "-----BEGIN PRIVATE KEY-----\n" + Chunk(b64) + "-----END PRIVATE KEY-----\n";
    }

    private static string EnsurePem(string cert, string label)
        => cert.Contains("BEGIN", StringComparison.Ordinal)
            ? cert
            : $"-----BEGIN {label}-----\n{Chunk(cert.Trim())}-----END {label}-----\n";

    private static string Chunk(string b64)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < b64.Length; i += 64)
            sb.AppendLine(b64.Substring(i, Math.Min(64, b64.Length - i)));
        return sb.ToString();
    }

    private static async Task<string> ResignAsync(
        string ipaPath, string profilePath, string keyPemPath, string certPath,
        Action<string> log, CancellationToken ct)
    {
        string? zsign = DeviceService.FindTool("zsign.exe")
            ?? throw new FileNotFoundException(
                "zsign.exe not found. Put a zsign Windows build in SideLoadr\\tools or on PATH (see README).");

        string outPath = Path.Combine(
            Path.GetDirectoryName(ipaPath)!,
            Path.GetFileNameWithoutExtension(ipaPath) + "-signed.ipa");
        string args = $"-k \"{keyPemPath}\" -m \"{profilePath}\" -c \"{certPath}\" -o \"{outPath}\" \"{ipaPath}\"";
        log("Resigning with zsign...");

        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(zsign, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!p.Start())
            throw new InvalidOperationException("Could not start zsign.");
        Task<string> stdOut = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stdErr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        string output = (await stdOut).Trim();
        if (output.Length > 0)
            log(output);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"zsign failed (exit {p.ExitCode}). {(await stdErr).Trim()}".Trim());
        log($"Signed: {outPath}");
        return outPath;
    }
}
