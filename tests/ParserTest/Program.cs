using SideLoadr;
using SideLoadr.Services;

int failures = 0;
void Check(bool cond, string name)
{
    Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
    if (!cond) failures++;
}

var bin = PlistParser.Parse(File.ReadAllBytes(@"D:\SideLoadr\tests\assets_bin.plist"));
Check((string)bin["CFBundleIdentifier"]! == "com.test.app", "bin bundle id");
Check((long)bin["Count"]! == 42, "bin int");
Check((bool)bin["Flag"]! == true, "bin bool");
Check(((List<object?>)bin["Items"]!).Count == 2, "bin array");
Check((long)((Dictionary<string, object?>)bin["Nested"]!)["x"]! == 1, "bin nested dict");

var xml = PlistParser.Parse(File.ReadAllBytes(@"D:\SideLoadr\tests\assets_xml.plist"));
Check((string)xml["CFBundleIdentifier"]! == "com.test.app", "xml bundle id");
Check((long)xml["Count"]! == 42, "xml int");
Check((bool)xml["Flag"]! == true, "xml bool");

IpaInfo ipa = IpaReader.Read(@"D:\SideLoadr\tests\test.ipa");
Check(ipa.BundleId == "com.test.app", "ipa bundle id");
Check(ipa.BundleName == "TestApp", "ipa name");
Check(ipa.Version == "1.2", "ipa version");
Check(ipa.MinOs == "16.0", "ipa min os");

// writer roundtrip
var round = new Dictionary<string, object?> { ["A"] = "x", ["N"] = (long)7, ["B"] = true, ["L"] = new List<object?> { "a", (long)1 } };
var back = PlistParser.Parse(PlistWriter.WriteXml(round));
Check((string)back["A"]! == "x", "writer roundtrip string");
Check((long)back["N"]! == 7, "writer roundtrip int");
Check((bool)back["B"]! == true, "writer roundtrip bool");
Check(((List<object?>)back["L"]!).Count == 2, "writer roundtrip array");

// clone
var clone = IpaCustomizer.CloneIpa(@"D:\SideLoadr\tests\test.ipa", "com.test.app.clone", "TestApp 2");
IpaInfo cloned = IpaReader.Read(clone.Path);
Check(cloned.BundleId == "com.test.app.clone", "clone bundle id");
Check(cloned.BundleName == "TestApp 2", "clone name");
Check(clone.Path.Contains("-clone"), "clone filename");
File.Delete(clone.Path);

// clone defaults
var cloneAuto = IpaCustomizer.CloneIpa(@"D:\SideLoadr\tests\test.ipa", "", "");
IpaInfo clonedAuto = IpaReader.Read(cloneAuto.Path);
Check(clonedAuto.BundleId == "com.test.app.clone", "clone auto bundle id");
Check(clonedAuto.BundleName == "TestApp 2", "clone auto name");
File.Delete(cloneAuto.Path);

// bad bundle id
bool threw = false;
try { IpaCustomizer.CloneIpa(@"D:\SideLoadr\tests\test.ipa", "bad id!", null); }
catch (InvalidDataException) { threw = true; }
Check(threw, "clone rejects bad bundle id");

// rename only
var ren = IpaCustomizer.CustomizeIpa(@"D:\SideLoadr\tests\test.ipa", null, "Renamed", null);
Check(IpaReader.Read(ren.Path).BundleName == "Renamed", "customize rename");
Check(IpaReader.Read(ren.Path).BundleId == "com.test.app", "customize keeps id");
File.Delete(ren.Path);

// icon swap
var icon = IpaCustomizer.CustomizeIpa(@"D:\SideLoadr\tests\test-icon.ipa", null, null, @"D:\SideLoadr\tests\newicon.png");
using (var zip = System.IO.Compression.ZipFile.OpenRead(icon.Path))
{
    var e2 = zip.GetEntry("Payload/IconApp.app/AppIcon60x60@2x.png")!;
    using var s = e2.Open();
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    Check(ms.ToArray().SequenceEqual(File.ReadAllBytes(@"D:\SideLoadr\tests\newicon.png")), "icon 2x replaced");
    var e3 = zip.GetEntry("Payload/IconApp.app/AppIcon60x60@3x.png")!;
    using var s3 = e3.Open();
    using var ms3 = new MemoryStream();
    s3.CopyTo(ms3);
    Check(ms3.ToArray().SequenceEqual(File.ReadAllBytes(@"D:\SideLoadr\tests\newicon.png")), "icon 3x replaced");
}
Check(icon.Notes.Any(n => n.StartsWith("icon replaced:")), "icon notes logged");
File.Delete(icon.Path);

// update check logic
Check(UpdateService.IsNewer("v0.3.0", "0.2.0"), "update newer");
Check(!UpdateService.IsNewer("v0.2.0", "0.2.0"), "update same");
Check(!UpdateService.IsNewer("v0.1.0", "0.2.0"), "update older");

// CLI parsing
var cli1 = CliParser.Parse(new[] { "--refresh-all", "--udid", "ABC" });
Check(cli1.Mode == "refresh-all" && cli1.Udid == "ABC", "cli refresh-all");
var cli2 = CliParser.Parse(new[] { "--install", "a.ipa" });
Check(cli2.Mode == "install" && cli2.IpaPath == "a.ipa", "cli install");
Check(CliParser.IsCli(new[] { "--help" }), "cli detected");
Check(!CliParser.IsCli(Array.Empty<string>()), "gui detected");
bool usage = false;
try { CliParser.Parse(new[] { "--bogus" }); } catch (UsageException) { usage = true; }
Check(usage, "cli rejects unknown");

// watch-folder loop guard
Check(WatchFolderService.ShouldProcess(@"D:\SideLoadr\tests\test.ipa"), "watch accepts ipa");
string signedCopy = @"D:\SideLoadr\tests\tmp-signed.ipa";
File.Copy(@"D:\SideLoadr\tests\test.ipa", signedCopy, true);
Check(!WatchFolderService.ShouldProcess(signedCopy), "watch ignores own output");
string cloneCopy = @"D:\SideLoadr\tests\tmp-clone2.ipa";
File.Copy(@"D:\SideLoadr\tests\test.ipa", cloneCopy, true);
Check(!WatchFolderService.ShouldProcess(cloneCopy), "watch ignores clone output");
File.Delete(signedCopy);
File.Delete(cloneCopy);

// credential vault roundtrip (unique target, cleaned up)
string target = "SideLoadrTest/" + Guid.NewGuid().ToString("N");
CredentialStore.Save("test@example.com", "s3cret", target);
bool loaded = CredentialStore.TryLoad(out string cid, out string cpw, target);
Check(loaded && cid == "test@example.com" && cpw == "s3cret", "credential roundtrip");
CredentialStore.Delete(target);
Check(!CredentialStore.TryLoad(out _, out _, target), "credential deleted");

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURES");
return failures;
