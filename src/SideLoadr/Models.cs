namespace SideLoadr;

/// <summary>iOS device detected on this PC.</summary>
public sealed record DeviceInfo(string Name, string? Udid, string Status);

/// <summary>Metadata read out of an IPA file.</summary>
public sealed record IpaInfo(string FilePath, string BundleId, string BundleName, string Version, string? MinOs);

/// <summary>Record of an installed app used for 7-day expiry tracking.</summary>
public sealed record SideloadedApp(string BundleId, string BundleName, string Udid, DateTimeOffset InstalledAt, DateTimeOffset ExpiresAt)
{
    public int DaysLeft => (int)Math.Ceiling((ExpiresAt - DateTimeOffset.Now).TotalDays);
}

/// <summary>Non-secret persisted settings (never store passwords).</summary>
public sealed record AppSettings(string AppleId, string AnisetteUrl, string? LastUdid);
