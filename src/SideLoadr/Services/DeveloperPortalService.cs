using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace SideLoadr.Services;

/// <summary>Talks to the Apple Developer Portal APIs with an authenticated session:
/// registers the App ID + device, issues a development certificate from our CSR,
/// and downloads a provisioning profile. Free accounts get 7-day profiles.</summary>
public sealed class DeveloperPortalService(HttpClient http, AppleSession session)
{
    private const string Base = "https://developerservices2.apple.com/services/v1";

    public async Task EnsureAppIdAsync(string bundleId, Action<string> log, CancellationToken ct)
    {
        log($"Registering App ID {bundleId}...");
        using var resp = await http.PostAsJsonAsync($"{Base}/bundleIds", new
        {
            teamId = session.TeamId,
            identifier = bundleId,
            name = bundleId,
            type = "explicit",
        }, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode && !body.Contains("already", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"App ID registration failed ({(int)resp.StatusCode}). {body[..Math.Min(300, body.Length)]}");
        log("App ID ready.");
    }

    public async Task EnsureDeviceAsync(string udid, string deviceName, Action<string> log, CancellationToken ct)
    {
        log($"Registering device {udid}...");
        using var resp = await http.PostAsJsonAsync($"{Base}/devices", new
        {
            teamId = session.TeamId,
            udid,
            name = deviceName,
            platform = "ios",
        }, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode && !body.Contains("already", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Device registration failed ({(int)resp.StatusCode}). {body[..Math.Min(300, body.Length)]}");
        log("Device ready.");
    }

    /// <summary>Submits a PKCS#10 CSR and returns the issued development certificate (PEM).</summary>
    public async Task<string> FetchDevelopmentCertAsync(byte[] csrDer, Action<string> log, CancellationToken ct)
    {
        log("Requesting development certificate...");
        using var resp = await http.PostAsJsonAsync($"{Base}/certificates", new
        {
            teamId = session.TeamId,
            csrContent = Convert.ToBase64String(csrDer),
            certificateType = "IOS_DEVELOPMENT",
        }, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Certificate request failed ({(int)resp.StatusCode}). {body[..Math.Min(300, body.Length)]}");
        using var doc = JsonDocument.Parse(body);
        string cert = doc.RootElement.TryGetProperty("certificateContent", out var c)
            ? c.GetString() ?? throw new InvalidDataException("Empty certificate in response.")
            : throw new InvalidDataException("Unexpected certificate response: " + body[..Math.Min(300, body.Length)]);
        log("Certificate issued.");
        return cert;
    }

    /// <summary>Downloads the (7-day, free account) provisioning profile. Returns its local path.</summary>
    public async Task<string> FetchProfileAsync(string bundleId, Action<string> log, CancellationToken ct)
    {
        log("Downloading provisioning profile (7-day)...");
        using var resp = await http.GetAsync($"{Base}/profiles?teamId={Uri.EscapeDataString(session.TeamId)}", ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Profile download failed ({(int)resp.StatusCode}). {body[..Math.Min(300, body.Length)]}");

        string? profileB64 = null;
        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    if (p.TryGetProperty("bundleId", out var b) &&
                        string.Equals(b.GetString(), bundleId, StringComparison.OrdinalIgnoreCase) &&
                        p.TryGetProperty("profileContent", out var pc))
                    {
                        profileB64 = pc.GetString();
                        break;
                    }
                }
            }
        }
        if (profileB64 is null)
            throw new InvalidOperationException("No provisioning profile found for this App ID. " +
                "Free accounts are limited to ~10 App IDs and 3 concurrent apps — delete an old one and retry.");

        string dir = Path.Combine(Path.GetTempPath(), "SideLoadr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string profilePath = Path.Combine(dir, "dev.mobileprovision");
        await File.WriteAllBytesAsync(profilePath, Convert.FromBase64String(profileB64), ct);
        log("Profile downloaded.");
        return profilePath;
    }
}
