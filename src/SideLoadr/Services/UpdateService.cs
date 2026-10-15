using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SideLoadr.Services;

/// <summary>Checks GitHub releases for a newer SideLoadr. Best effort:
/// never throws, just logs (network here is flaky).</summary>
public static class UpdateService
{
    public static async Task CheckForUpdatesAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SideLoadr");
            string json = await http.GetStringAsync(
                "https://api.github.com/repos/okaygamer795-coder/Spryzen/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? t.GetString() ?? "" : "";
            string current = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            if (tag.Length > 0 && IsNewer(tag, current))
                log($"Update available: {tag} (you have v{current}). Get it at github.com/okaygamer795-coder/Spryzen/releases");
            else
                log("SideLoadr is up to date.");
        }
        catch (Exception ex)
        {
            log($"Update check skipped ({ex.Message}).");
        }
    }

    internal static bool IsNewer(string tag, string current)
    {
        if (Version.TryParse(tag.TrimStart('v', 'V'), out var latest) &&
            Version.TryParse(current.TrimStart('v', 'V'), out var mine))
            return latest > mine;
        return !string.Equals(tag.TrimStart('v', 'V'), current.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);
    }
}
