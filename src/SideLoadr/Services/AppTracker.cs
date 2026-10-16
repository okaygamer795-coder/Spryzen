using System.IO;
using System.Text.Json;

namespace SideLoadr.Services;

/// <summary>Tracks sideloaded apps and their 7-day free-cert expiry, plus
/// non-secret settings. Passwords are never persisted.</summary>
public static class AppTracker
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string StoreDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideLoadr");

    private static string AppsPath => Path.Combine(StoreDir, "apps.json");
    private static string SettingsPath => Path.Combine(StoreDir, "settings.json");
    private static string SetupFlagPath => Path.Combine(StoreDir, "setup.done");

    /// <summary>Whether the first-run wizard was dismissed (separate tiny file
    /// so old settings.json files keep working untouched).</summary>
    public static bool IsSetupDone()
    {
        try { return File.Exists(SetupFlagPath); } catch { return false; }
    }

    public static void MarkSetupDone(bool done)
    {
        try
        {
            if (done)
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(SetupFlagPath, "1");
            }
            else if (File.Exists(SetupFlagPath))
            {
                File.Delete(SetupFlagPath);
            }
        }
        catch
        {
            // best effort only
        }
    }

    public static List<SideloadedApp> Load()
    {
        try
        {
            if (!File.Exists(AppsPath))
                return new List<SideloadedApp>();
            return JsonSerializer.Deserialize<List<SideloadedApp>>(File.ReadAllText(AppsPath)) ?? new List<SideloadedApp>();
        }
        catch
        {
            return new List<SideloadedApp>();
        }
    }

    public static void RecordInstalled(string bundleId, string name, string? udid, string ipaPath = "")
    {
        var apps = Load();
        apps.RemoveAll(a => a.BundleId == bundleId && a.Udid == (udid ?? ""));
        var now = DateTimeOffset.Now;
        apps.Add(new SideloadedApp(bundleId, name, udid ?? "", now, now.AddDays(7), ipaPath ?? ""));
        Save(apps);
    }

    public static void Remove(string bundleId, string udid)
    {
        var apps = Load();
        apps.RemoveAll(a => a.BundleId == bundleId && a.Udid == udid);
        Save(apps);
    }

    private static void Save(List<SideloadedApp> apps)
    {
        Directory.CreateDirectory(StoreDir);
        File.WriteAllText(AppsPath, JsonSerializer.Serialize(apps, Json));
    }

    /// <summary>Well-known public anisette server (run by the SideStore team).
    /// Verify/replace it in Settings if it ever stops working — or self-host (see README).</summary>
    public const string DefaultAnisetteUrl = "https://ani.sidestore.io/v1/";

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (s is not null)
                    return string.IsNullOrWhiteSpace(s.AnisetteUrl)
                        ? s with { AnisetteUrl = DefaultAnisetteUrl }
                        : s;
            }
        }
        catch
        {
            // fall through to defaults
        }
        return new AppSettings("", DefaultAnisetteUrl, null);
    }

    public static void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(StoreDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Json));
    }
}
