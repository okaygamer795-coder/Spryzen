using System.IO;
using System.IO.Compression;

namespace SideLoadr.Services;

/// <summary>Reads metadata out of an .ipa (which is just a zip).</summary>
public static class IpaReader
{
    public static IpaInfo Read(string ipaPath)
    {
        var dict = ReadPlist(ipaPath);
        string bundleId = GetString(dict, "CFBundleIdentifier")
            ?? throw new InvalidDataException("Info.plist has no CFBundleIdentifier.");
        string name = GetString(dict, "CFBundleDisplayName")
            ?? GetString(dict, "CFBundleName")
            ?? Path.GetFileNameWithoutExtension(ipaPath);
        string version = GetString(dict, "CFBundleShortVersionString")
            ?? GetString(dict, "CFBundleVersion")
            ?? "?";

        return new IpaInfo(ipaPath, bundleId, name, version, GetString(dict, "MinimumOSVersion"));
    }

    /// <summary>Returns the raw Info.plist dictionary (for tools that need
    /// more than <see cref="Read"/> exposes, e.g. the executable name).</summary>
    public static Dictionary<string, object?> ReadPlist(string ipaPath)
    {
        if (!File.Exists(ipaPath))
            throw new FileNotFoundException("IPA not found.", ipaPath);

        using var zip = ZipFile.OpenRead(ipaPath);
        var entry = zip.Entries.FirstOrDefault(e => IsAppPlist(e.FullName))
            ?? throw new InvalidDataException("No Payload/*.app/Info.plist found — not a valid IPA.");

        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return PlistParser.Parse(ms.ToArray());
    }

    private static bool IsAppPlist(string fullName)
    {
        if (!fullName.StartsWith("Payload/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!fullName.EndsWith(".app/Info.plist", StringComparison.OrdinalIgnoreCase))
            return false;
        return fullName.Split('/').Length == 3;
    }

    private static string? GetString(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var v) ? v?.ToString() : null;
}
