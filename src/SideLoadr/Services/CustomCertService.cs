using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SideLoadr.Services;

/// <summary>User-supplied certificates (paid Apple ID): reads a .p12 and splits
/// it into the key/cert PEM files the resign step needs.
///
/// Two extraction paths: .NET crypto first, OpenSSL binary as fallback
/// (some machines refuse to export PFX-imported CNG keys — e.g. without a
/// fully loaded user profile — while OpenSSL reads the file directly).
/// Temp files are wiped afterwards; the Windows cert store is cleaned too.</summary>
public static class CustomCertService
{
    public static (string KeyPemPath, string CertPemPath) ExtractPems(
        byte[] pfx, string password, string? dir = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(pfx);
        dir ??= Path.Combine(Path.GetTempPath(), "SideLoadr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        string pfxPath = Path.Combine(dir, "custom.p12");
        File.WriteAllBytes(pfxPath, pfx);
        try
        {
            try
            {
                return ExtractViaDotNet(pfx, password, dir, log);
            }
            catch (CryptographicException ex) when (IsExportProblem(ex))
            {
                log?.Invoke("Built-in crypto refused the export, trying OpenSSL...");
                return ExtractViaOpenSsl(pfxPath, password, dir);
            }
        }
        finally
        {
            try { File.Delete(pfxPath); } catch { /* best effort */ }
        }
    }

    private static bool IsExportProblem(CryptographicException ex)
        => ex.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase);

    private static (string, string) ExtractViaDotNet(byte[] pfx, string password, string dir, Action<string>? log)
    {
        X509Certificate2 cert;
        try
        {
            cert = new X509Certificate2(pfx, password, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Could not open the .p12 (wrong password or corrupt file).", ex);
        }
        try
        {
            if (!cert.HasPrivateKey)
                throw new InvalidDataException("Certificate has no private key — export the .p12 WITH its key.");
            using RSA? rsa = cert.GetRSAPrivateKey();
            if (rsa is null)
                throw new InvalidDataException("Only RSA certificates are supported.");
            string keyPath = Path.Combine(dir, "custom-key.pem");
            string certPath = Path.Combine(dir, "custom-cert.pem");
            File.WriteAllText(keyPath, PemEncode("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()));
            File.WriteAllText(certPath, PemEncode("CERTIFICATE", cert.Export(X509ContentType.Cert)));
            log?.Invoke("Certificate opened (built-in crypto).");
            return (keyPath, certPath);
        }
        finally
        {
            try
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                store.Remove(cert);
            }
            catch
            {
                // best effort cleanup; leftovers stay DPAPI-encrypted in the user store
            }
            cert.Dispose();
        }
    }

    private static (string, string) ExtractViaOpenSsl(string pfxPath, string password, string dir)
    {
        string? openssl = FindOpenSsl()
            ?? throw new InvalidDataException(
                "Could not read the .p12: built-in export is blocked on this PC and no openssl binary was found. " +
                "Install Git for Windows (bundles openssl) and retry.");
        string keyPath = Path.Combine(dir, "custom-key.pem");
        string certPath = Path.Combine(dir, "custom-cert.pem");
        RunOpenSsl(openssl, $"pkcs12 -in \"{pfxPath}\" -nodes -nocerts -passin pass:{EscapeArg(password)}", keyPath);
        RunOpenSsl(openssl, $"pkcs12 -in \"{pfxPath}\" -nodes -nokeys -clcerts -passin pass:{EscapeArg(password)}", certPath);
        if (!File.ReadAllText(keyPath).Contains("BEGIN PRIVATE KEY") ||
            !File.ReadAllText(certPath).Contains("BEGIN CERTIFICATE"))
            throw new InvalidDataException("OpenSSL could not read the .p12 (wrong password or corrupt file).");
        return (keyPath, certPath);
    }

    private static string? FindOpenSsl()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), "openssl.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore malformed PATH entries
            }
        }
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string candidate in new[]
        {
            Path.Combine(localAppData, @"hermes\git\usr\bin\openssl.exe"),
            Path.Combine(localAppData, @"hermes\git\mingw64\bin\openssl.exe"),
            @"C:\Program Files\Git\usr\bin\openssl.exe",
            @"C:\Program Files\Git\mingw64\bin\openssl.exe",
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static void RunOpenSsl(string openssl, string args, string outPath)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(openssl, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!p.Start())
            throw new InvalidDataException("Could not start openssl.");
        Task<string> stdOut = p.StandardOutput.ReadToEndAsync();
        Task<string> stdErr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(); } catch { /* already exiting */ }
            throw new InvalidDataException("openssl timed out reading the .p12.");
        }
        File.WriteAllText(outPath, stdOut.Result);
        if (p.ExitCode != 0)
            throw new InvalidDataException($"openssl failed: {stdErr.Result.Trim()}".Trim());
    }

    private static string EscapeArg(string s) => "\"" + s.Replace("\"", "") + "\"";

    private static string PemEncode(string label, byte[] der)
    {
        string b64 = Convert.ToBase64String(der);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-----BEGIN {label}-----");
        for (int i = 0; i < b64.Length; i += 64)
            sb.AppendLine(b64.Substring(i, Math.Min(64, b64.Length - i)));
        sb.AppendLine($"-----END {label}-----");
        return sb.ToString();
    }
}
