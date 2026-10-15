using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace SideLoadr.Services;

/// <summary>Authenticated Apple Developer session (free account).</summary>
public sealed class AppleSession
{
    public required string AppleId { get; init; }
    public required string TeamId { get; init; }
    public required string TeamName { get; init; }
    public required string SessionToken { get; init; }
}

/// <summary>Apple ID login for the free-provisioning flow (same shape as
/// AltStore/Sideloadly: Apple ID + password, 2FA code when prompted).
/// Passwords are kept in memory only and never written to disk.</summary>
public sealed class AppleAuthService(HttpClient http, string anisetteServerUrl)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Fetches anisette data without logging in — used by the Test button.</summary>
    public static async Task<Dictionary<string, string>> FetchAnisetteAsync(string serverUrl, CancellationToken ct)
    {
        using var http = new HttpClient();
        return await new AppleAuthService(http, serverUrl).GetAnisetteDataAsync(ct);
    }

    public async Task<AppleSession> LoginAsync(
        string appleId,
        string password,
        Func<Task<string>> request2faCode,
        Action<string> log,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (string.IsNullOrWhiteSpace(anisetteServerUrl))
            throw new InvalidOperationException(
                "Set the Anisette server URL first (see README). Free signing needs " +
                "anisette provisioning data, which can only be generated with Apple hardware.");

        log("Fetching anisette provisioning data...");
        Dictionary<string, string> anisette = await GetAnisetteDataAsync(ct);
        log("Anisette OK.");

        log("Signing in to Apple ID...");
        using var signin = new HttpRequestMessage(HttpMethod.Post, "https://idmsa.apple.com/appleauth/auth/signin");
        signin.Headers.Add("X-Apple-Anisette-Headers", Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(anisette))));
        signin.Content = JsonContent.Create(new { accountName = appleId, password, rememberMe = false });
        using var resp = await http.SendAsync(signin, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict || body.Contains("twoFactor", StringComparison.OrdinalIgnoreCase))
        {
            log("Apple requires a 2FA code — enter it in the 2FA box and run again.");
            string code = (await request2faCode()).Trim();
            ArgumentException.ThrowIfNullOrWhiteSpace(code, nameof(code));
            await Submit2faCodeAsync(appleId, code, anisette, log, ct);
        }
        else if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Apple sign-in failed ({(int)resp.StatusCode}). {Truncate(body)} " +
                "Use an app-specific password if you have 2FA on, or check the README.");
        }

        log("Fetching developer teams...");
        AppleSession session = await FetchFreeTeamAsync(appleId, log, ct);
        log($"Signed in as {session.AppleId} (team {session.TeamName}).");
        return session;
    }

    private async Task<Dictionary<string, string>> GetAnisetteDataAsync(CancellationToken ct)
    {
        // Different anisette servers expose the data at different paths
        // (e.g. "/anisette" vs the server root). Try both, first success wins.
        string baseUrl = anisetteServerUrl.TrimEnd('/');
        string[] candidates = baseUrl.EndsWith("/anisette", StringComparison.OrdinalIgnoreCase)
            ? new[] { baseUrl }
            : new[] { baseUrl + "/anisette", baseUrl };
        var errors = new List<string>();
        foreach (string url in candidates)
        {
            try
            {
                using var resp = await http.GetAsync(url, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"{url} -> {(int)resp.StatusCode}");
                    continue;
                }
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(body, Json);
                if (data is { Count: > 0 })
                {
                    LogKeysIfVerbose(data);
                    return data;
                }
                errors.Add($"{url} -> empty/unexpected JSON");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                errors.Add($"{url} -> {ex.Message}");
            }
        }
        throw new InvalidOperationException(
            "Anisette server gave no usable data (" + string.Join("; ", errors) +
            "). Check the URL or run your own anisette server (see README).");
    }

    private static void LogKeysIfVerbose(Dictionary<string, string> data)
    {
        // Keys are logged by the caller flow; kept separate for future diagnostics.
        _ = data.Count;
    }

    private async Task Submit2faCodeAsync(string appleId, string code, Dictionary<string, string> anisette, Action<string> log, CancellationToken ct)
    {
        log("Submitting 2FA code...");
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://idmsa.apple.com/appleauth/auth/verify/trusteddevice/securitycode");
        req.Content = JsonContent.Create(new { securityCode = new { code } });
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"2FA verification failed ({(int)resp.StatusCode}). {Truncate(await resp.Content.ReadAsStringAsync(ct))}");
        log("2FA accepted.");
    }

    private async Task<AppleSession> FetchFreeTeamAsync(string appleId, Action<string> log, CancellationToken ct)
    {
        using var resp = await http.GetAsync(
            "https://developerservices2.apple.com/services/v1/teams", ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not list developer teams ({(int)resp.StatusCode}). " +
                "This step needs validating against a real free Apple ID — see README. " + Truncate(body));

        using var doc = JsonDocument.Parse(body);
        foreach (var team in doc.RootElement.EnumerateArray())
        {
            string type = team.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type.Contains("free", StringComparison.OrdinalIgnoreCase) || type.Contains("personal", StringComparison.OrdinalIgnoreCase))
            {
                return new AppleSession
                {
                    AppleId = appleId,
                    TeamId = team.GetProperty("teamId").GetString() ?? throw new InvalidDataException("Team has no ID."),
                    TeamName = team.TryGetProperty("name", out var n) ? n.GetString() ?? team.GetProperty("teamId").GetString()! : team.GetProperty("teamId").GetString()!,
                    SessionToken = "cookie-session",
                };
            }
        }
        throw new InvalidOperationException("No free/personal team found on this Apple ID.");
    }

    private static string Truncate(string s, int max = 300)
        => s.Length <= max ? s : s[..max] + "…";
}
