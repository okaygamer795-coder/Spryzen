using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace SideLoadr.Services;

public sealed record CustomizeResult(string Path, List<string> Notes);

/// <summary>Community wishlist: app cloning + IPA customizing, done as pure
/// zip/plist surgery (no device needed). Output still needs signing after.</summary>
public static class IpaCustomizer
{
    private static readonly Regex BundleIdRx = new(@"^[A-Za-z0-9][A-Za-z0-9.\-]*$", RegexOptions.Compiled);
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Copies the IPA with a new bundle ID + display name so it installs
    /// as a separate app (dual accounts, etc.). Blank inputs get sane defaults.</summary>
    public static CustomizeResult CloneIpa(string srcPath, string? newBundleId, string? newName)
    {
        var info = IpaReader.Read(srcPath);
        string bid = string.IsNullOrWhiteSpace(newBundleId) ? info.BundleId + ".clone" : newBundleId.Trim();
        ValidateBundleId(bid);
        string name = string.IsNullOrWhiteSpace(newName) ? info.BundleName + " 2" : newName.Trim();
        string dest = NextTo(srcPath, "-clone");
        return Rewrite(srcPath, dest, dict =>
        {
            dict["CFBundleIdentifier"] = bid;
            dict["CFBundleDisplayName"] = name;
            dict["CFBundleName"] = name;
            return new List<string> { $"bundle id -> {bid}", $"name -> {name}" };
        });
    }

    /// <summary>Renames bundle ID / display name and optionally swaps the app icon.</summary>
    public static CustomizeResult CustomizeIpa(string srcPath, string? bundleId, string? displayName, string? iconPngPath)
    {
        if (string.IsNullOrWhiteSpace(bundleId) && string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(iconPngPath))
            throw new InvalidOperationException("Nothing to customize — fill in at least one field.");
        if (!string.IsNullOrWhiteSpace(bundleId))
            ValidateBundleId(bundleId.Trim());
        byte[]? iconBytes = null;
        if (!string.IsNullOrWhiteSpace(iconPngPath))
        {
            if (!File.Exists(iconPngPath))
                throw new FileNotFoundException("Icon file not found.", iconPngPath);
            iconBytes = File.ReadAllBytes(iconPngPath);
            if (iconBytes.Length < 8 || !iconBytes.Take(8).SequenceEqual(PngMagic))
                throw new InvalidDataException("Icon must be a PNG file.");
        }
        string dest = NextTo(srcPath, "-custom");
        return Rewrite(srcPath, dest, dict =>
        {
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(bundleId))
            {
                dict["CFBundleIdentifier"] = bundleId.Trim();
                notes.Add($"bundle id -> {bundleId.Trim()}");
            }
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                dict["CFBundleDisplayName"] = displayName.Trim();
                dict["CFBundleName"] = displayName.Trim();
                notes.Add($"name -> {displayName.Trim()}");
            }
            return notes;
        }, iconBytes);
    }

    private static void ValidateBundleId(string bid)
    {
        if (!BundleIdRx.IsMatch(bid))
            throw new InvalidDataException(
                $"Bad bundle ID '{bid}'. Use reverse-DNS style: letters, digits, dots, dashes (e.g. com.example.app.clone).");
    }

    private static string NextTo(string srcPath, string suffix)
    {
        string dir = Path.GetDirectoryName(srcPath)!;
        string stem = Path.GetFileNameWithoutExtension(srcPath);
        string dest = Path.Combine(dir, stem + suffix + ".ipa");
        for (int i = 2; File.Exists(dest); i++)
            dest = Path.Combine(dir, $"{stem}{suffix}{i}.ipa");
        return dest;
    }

    private static CustomizeResult Rewrite(
        string srcPath, string destPath,
        Func<Dictionary<string, object?>, List<string>> patch,
        byte[]? iconBytes = null)
    {
        var notes = new List<string>();
        using var src = ZipFile.OpenRead(srcPath);
        var plistEntry = src.Entries.FirstOrDefault(e => IsAppPlist(e.FullName))
            ?? throw new InvalidDataException("No Payload/*.app/Info.plist found — not a valid IPA.");
        string plistName = plistEntry.FullName;
        string appDir = plistName[..^"Info.plist".Length];

        byte[] origPlist;
        using (var s = plistEntry.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            origPlist = ms.ToArray();
        }
        var dict = PlistParser.Parse(origPlist);
        notes.AddRange(patch(dict));
        byte[] newPlist = PlistWriter.WriteXml(dict);

        List<string> iconNames = iconBytes is null ? new List<string>() : FindIconNames(dict);
        using var dest = ZipFile.Open(destPath, ZipArchiveMode.Create);
        foreach (var entry in src.Entries)
        {
            if (entry.FullName.Equals(plistName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (iconBytes is not null && IsIconFile(entry.FullName, appDir, iconNames))
            {
                var outEntry = dest.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var w = outEntry.Open();
                w.Write(iconBytes, 0, iconBytes.Length);
                notes.Add($"icon replaced: {entry.FullName}");
                continue;
            }
            // skip directories (entries ending in / have no data)
            if (entry.FullName.EndsWith('/'))
                continue;
            var copy = dest.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            using var input = entry.Open();
            using var output = copy.Open();
            input.CopyTo(output);
        }
        var plistOut = dest.CreateEntry(plistName, CompressionLevel.Optimal);
        using (var w = plistOut.Open())
            w.Write(newPlist, 0, newPlist.Length);

        if (iconBytes is not null && !notes.Any(n => n.StartsWith("icon replaced:")))
            throw new InvalidDataException(
                "No replaceable icon file found in this IPA (looked for CFBundleIcons entries). Try another IPA.");
        notes.Insert(0, $"wrote {destPath}");
        return new CustomizeResult(destPath, notes);
    }

    /// <summary>Icon entries in the plist are base names ("AppIcon60x60") while the
    /// files carry scale + extension ("AppIcon60x60@2x.png"). Match accordingly,
    /// only inside the .app folder.</summary>
    private static bool IsIconFile(string entryName, string appDir, List<string> iconNames)
    {
        if (!entryName.StartsWith(appDir, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!entryName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return false;
        string file = Path.GetFileNameWithoutExtension(entryName);
        // strip @2x / @3x scale suffix
        int at = file.LastIndexOf('@');
        string stem = at > 0 ? file[..at] : file;
        return iconNames.Any(n =>
            n.Equals(file, StringComparison.OrdinalIgnoreCase) ||
            n.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
            (n + ".png").Equals(Path.GetFileName(entryName), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Injects a .dylib tweak: copies it into Frameworks and patches
    /// the main executable with an LC_LOAD_DYLIB. The result MUST be re-signed
    /// (injection invalidates the old signature by design).</summary>
    public static CustomizeResult InjectTweak(string srcPath, string dylibPath)
    {
        if (!File.Exists(dylibPath))
            throw new FileNotFoundException("Dylib not found.", dylibPath);
        if (!dylibPath.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tweak must be a .dylib file.");
        string dylibName = Path.GetFileName(dylibPath);
        if (dylibName.Any(c => c is '/' or '\\' or ':'))
            throw new InvalidDataException("Bad dylib file name.");

        var dict = IpaReader.ReadPlist(srcPath);
        string? exeName = dict.TryGetValue("CFBundleExecutable", out var e) ? e?.ToString() : null;
        string dest = NextTo(srcPath, "-tweaked");
        string loadPath = "@executable_path/Frameworks/" + dylibName;
        var notes = new List<string>();

        using var src = ZipFile.OpenRead(srcPath);
        var plistEntry = src.Entries.FirstOrDefault(en => IsAppPlist(en.FullName))
            ?? throw new InvalidDataException("No Payload/*.app/Info.plist found — not a valid IPA.");
        string appDir = plistEntry.FullName[..^"Info.plist".Length];

        string? exeEntryName = null;
        if (!string.IsNullOrEmpty(exeName))
            exeEntryName = src.Entries.FirstOrDefault(en =>
                en.FullName.Equals(appDir + exeName, StringComparison.OrdinalIgnoreCase))?.FullName;
        exeEntryName ??= src.Entries.FirstOrDefault(en =>
            en.FullName.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) &&
            !en.FullName.EndsWith('/') &&
            Path.GetFileName(en.FullName).IndexOf('.') < 0)?.FullName;
        if (exeEntryName is null)
            throw new InvalidDataException("Could not find the main executable in this IPA.");

        byte[] exeBytes;
        using (var s = src.GetEntry(exeEntryName)!.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            exeBytes = ms.ToArray();
        }
        var inject = MachOInjector.InjectIntoBytes(exeBytes, loadPath);
        notes.Add(inject.AlreadyPresent
            ? $"already loads {loadPath} ({inject.SlicesPatched} slice(s) checked)"
            : $"patched {inject.SlicesPatched} slice(s), load command +{inject.CommandSize} bytes");

        byte[] dylibBytes = File.ReadAllBytes(dylibPath);
        using var destZip = ZipFile.Open(dest, ZipArchiveMode.Create);
        foreach (var entry in src.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            var copy = destZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            using var input = entry.FullName.Equals(exeEntryName, StringComparison.OrdinalIgnoreCase)
                ? (Stream)new MemoryStream(exeBytes, writable: false)
                : entry.Open();
            using var output = copy.Open();
            input.CopyTo(output);
        }
        var dylibEntry = destZip.CreateEntry(appDir + "Frameworks/" + dylibName, CompressionLevel.Optimal);
        using (var w = dylibEntry.Open())
            w.Write(dylibBytes, 0, dylibBytes.Length);
        notes.Add($"bundled Frameworks/{dylibName}");
        notes.Insert(0, $"wrote {dest}");
        notes.Add("Re-sign before installing (injection invalidates the old signature).");
        return new CustomizeResult(dest, notes);
    }

    private static bool IsAppPlist(string fullName)
    {
        if (!fullName.StartsWith("Payload/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!fullName.EndsWith(".app/Info.plist", StringComparison.OrdinalIgnoreCase))
            return false;
        return fullName.Split('/').Length == 3;
    }

    private static List<string> FindIconNames(Dictionary<string, object?> dict)
    {
        var names = new List<string>();
        try
        {
            if (dict.TryGetValue("CFBundleIcons", out var icons) && icons is Dictionary<string, object?> iconsDict &&
                iconsDict.TryGetValue("CFBundlePrimaryIcon", out var primary) && primary is Dictionary<string, object?> primaryDict &&
                primaryDict.TryGetValue("CFBundleIconFiles", out var files) && files is List<object?> list)
            {
                names.AddRange(list.Select(v => v?.ToString()).Where(s => !string.IsNullOrEmpty(s))!);
            }
            if (dict.TryGetValue("CFBundleIconFiles", out var legacy) && legacy is List<object?> legacyList)
                names.AddRange(legacyList.Select(v => v?.ToString()).Where(s => !string.IsNullOrEmpty(s))!);
        }
        catch
        {
            // best effort only
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
