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

// mach-o tweak injection
static bool ContainsSeq(byte[] hay, byte[] needle)
{
    for (int i = 0; i + needle.Length <= hay.Length; i++)
    {
        bool match = true;
        for (int j = 0; j < needle.Length; j++)
            if (hay[i + j] != needle[j]) { match = false; break; }
        if (match) return true;
    }
    return false;
}
string loadPath = "@executable_path/Frameworks/fake.dylib";
byte[] loadBytes = System.Text.Encoding.UTF8.GetBytes(loadPath);
byte[] thin = File.ReadAllBytes(@"D:\SideLoadr\tests\macho_thin.bin");
int ncmdsBefore = BitConverter.ToInt32(thin, 16);
var inj = MachOInjector.InjectIntoBytes(thin, loadPath);
Check(inj.SlicesPatched == 1 && !inj.AlreadyPresent, "macho inject thin");
Check(BitConverter.ToInt32(thin, 16) == ncmdsBefore + 1, "macho ncmds bumped");
Check(ContainsSeq(thin, loadBytes), "macho load path present");
var inj2 = MachOInjector.InjectIntoBytes(thin, loadPath);
Check(inj2.AlreadyPresent && inj2.SlicesPatched == 0, "macho idempotent");
bool crypt = false;
try { MachOInjector.InjectIntoBytes(File.ReadAllBytes(@"D:\SideLoadr\tests\macho_crypt.bin"), loadPath); }
catch (InvalidDataException) { crypt = true; }
Check(crypt, "macho refuses encrypted");
bool nospace = false;
try { MachOInjector.InjectIntoBytes(File.ReadAllBytes(@"D:\SideLoadr\tests\macho_nospace.bin"), loadPath); }
catch (InvalidDataException) { nospace = true; }
Check(nospace, "macho refuses no-room");
var fat = MachOInjector.InjectIntoBytes(File.ReadAllBytes(@"D:\SideLoadr\tests\macho_fat.bin"), loadPath);
Check(fat.SlicesPatched == 2, "macho fat both slices");
// IPA-level injection
var tw = IpaCustomizer.InjectTweak(@"D:\SideLoadr\tests\test-tweak.ipa", @"D:\SideLoadr\tests\fake.dylib");
using (var zip = System.IO.Compression.ZipFile.OpenRead(tw.Path))
{
    var fw = zip.GetEntry("Payload/TweakApp.app/Frameworks/fake.dylib")!;
    using var fs = fw.Open();
    using var fms = new MemoryStream();
    fs.CopyTo(fms);
    Check(fms.ToArray().SequenceEqual(File.ReadAllBytes(@"D:\SideLoadr\tests\fake.dylib")), "tweak dylib bundled");
    var exe = zip.GetEntry("Payload/TweakApp.app/TweakApp")!;
    using var es = exe.Open();
    using var ems = new MemoryStream();
    es.CopyTo(ems);
    Check(ContainsSeq(ems.ToArray(), loadBytes), "tweak exe patched");
}
Check(tw.Notes.Any(n => n.Contains("Re-sign")), "inject notes resign");
Check(IpaReader.Read(tw.Path).BundleId == "com.test.tweakapp", "injected ipa still valid");
File.Delete(tw.Path);
// non-executable entry is rejected, not silently patched
bool noexe = false;
try { IpaCustomizer.InjectTweak(@"D:\SideLoadr\tests\test.ipa", @"D:\SideLoadr\tests\fake.dylib"); }
catch (Exception) { noexe = true; }
Check(noexe, "inject rejects bad exe");

// god mode supercharge
var god = IpaCustomizer.Supercharge(@"D:\SideLoadr\tests\test-god.ipa", new IpaCustomizer.SuperchargeOptions(
    "com.test.godapp.clone", "GodApp 2", @"D:\SideLoadr\tests\newicon.png", @"D:\SideLoadr\tests\fake.dylib",
    RelaxCompatibility: true, EnableFileSharing: true, MinOs: "14.0"));
var gdict = IpaReader.ReadPlist(god.Path);
Check(((List<object?>)gdict["UIDeviceFamily"]!).Select(v => (long)v!).SequenceEqual(new[] { 1L, 2L }), "god device family");
Check(!gdict.ContainsKey("UIRequiredDeviceCapabilities"), "god caps stripped");
Check((string)gdict["MinimumOSVersion"]! == "14.0", "god min ios");
Check((bool)gdict["UIFileSharingEnabled"]! == true, "god file sharing");
Check((bool)gdict["LSSupportsOpeningDocumentsInPlace"]! == true, "god open in place");
Check(IpaReader.Read(god.Path).BundleId == "com.test.godapp.clone", "god renamed");
using (var zip = System.IO.Compression.ZipFile.OpenRead(god.Path))
{
    var fw = zip.GetEntry("Payload/GodApp.app/Frameworks/fake.dylib")!;
    using var fs = fw.Open();
    using var fms = new MemoryStream();
    fs.CopyTo(fms);
    Check(fms.ToArray().SequenceEqual(File.ReadAllBytes(@"D:\SideLoadr\tests\fake.dylib")), "god dylib bundled");
    var gexe = zip.GetEntry("Payload/GodApp.app/GodApp")!;
    using var gs = gexe.Open();
    using var gms = new MemoryStream();
    gs.CopyTo(gms);
    Check(ContainsSeq(gms.ToArray(), loadBytes), "god exe patched");
    var gicon = zip.GetEntry("Payload/GodApp.app/AppIcon60x60@2x.png")!;
    using var gis = gicon.Open();
    using var gims = new MemoryStream();
    gis.CopyTo(gims);
    Check(gims.ToArray().SequenceEqual(File.ReadAllBytes(@"D:\SideLoadr\tests\newicon.png")), "god icon swapped");
}
Check(god.Notes.Any(n => n.Contains("Re-sign")), "god notes resign");
File.Delete(god.Path);
Check(IpaCustomizer.CompareVersions("14.0", "17.0") < 0, "version lt");
Check(IpaCustomizer.CompareVersions("17.0", "17.0") == 0, "version eq");
Check(IpaCustomizer.CompareVersions("18.1", "17.0") > 0, "version gt");

// custom certificate p12 roundtrip (self-signed, cleaned up)
string p12dir = Path.Combine(Path.GetTempPath(), "sideloadr-test-" + Guid.NewGuid().ToString("N"));
using (System.Security.Cryptography.RSA tkey = System.Security.Cryptography.RSA.Create(2048))
{
    var creq = new System.Security.Cryptography.X509Certificates.CertificateRequest(
        "CN=sideloadr-test", tkey,
        System.Security.Cryptography.HashAlgorithmName.SHA256,
        System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    using var selfsigned = creq.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
    byte[] pfx = selfsigned.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pw123");
    var (kpath, cpath) = CustomCertService.ExtractPems(pfx, "pw123", p12dir);
    Check(File.ReadAllText(kpath).Contains("BEGIN PRIVATE KEY"), "p12 key pem");
    Check(File.ReadAllText(cpath).Contains("BEGIN CERTIFICATE"), "p12 cert pem");
    bool badpw = false;
    try { CustomCertService.ExtractPems(pfx, "wrong", p12dir); }
    catch (InvalidDataException) { badpw = true; }
    Check(badpw, "p12 wrong password");
}
try { Directory.Delete(p12dir, true); } catch { }

// helper server (localhost, no device needed — validation happens first)
static string WriteTempIpa(string name, byte[] bytes)
{
    string path = Path.Combine(Path.GetTempPath(), "sideloadr-test-" + Guid.NewGuid().ToString("N") + "-" + name);
    File.WriteAllBytes(path, bytes);
    return path;
}
static int FreePort()
{
    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start();
    int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}
int hport = FreePort();
using (var hs = new HelperServer())
{
    hs.Start(hport,
        req => Task.FromResult(new HelperResult(false, "no such app: " + req.BundleId)),
        (name, bytes) =>
        {
            string tmp = WriteTempIpa(name, bytes);
            try
            {
                var info = IpaReader.Read(tmp);
                return Task.FromResult(new HelperResult(true, "ok " + info.BundleId));
            }
            catch (Exception ex) { return Task.FromResult(new HelperResult(false, ex.Message)); }
            finally { try { File.Delete(tmp); } catch { } }
        },
        _ => { });
    using var http = new System.Net.Http.HttpClient();
    string st = await http.GetStringAsync($"http://127.0.0.1:{hport}/status");
    Check(st.Contains("\"ok\":true"), "helper status");
    var r404 = await http.GetAsync($"http://127.0.0.1:{hport}/nope");
    Check(r404.StatusCode == System.Net.HttpStatusCode.NotFound, "helper 404");
    var badRefresh = await http.PostAsync($"http://127.0.0.1:{hport}/refresh",
        new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
    Check(badRefresh.StatusCode == System.Net.HttpStatusCode.BadRequest, "helper bad refresh");
    var unk = await http.PostAsync($"http://127.0.0.1:{hport}/refresh",
        new System.Net.Http.StringContent("{\"udid\":\"\",\"bundleID\":\"com.nope\",\"profile\":\"\",\"ipa_sha256\":\"\"}", System.Text.Encoding.UTF8, "application/json"));
    Check((await unk.Content.ReadAsStringAsync()).Contains("no such app"), "helper unknown app");
    var badIpa = await http.PostAsync($"http://127.0.0.1:{hport}/install?filename=x.ipa",
        new System.Net.Http.ByteArrayContent(new byte[] { 1, 2, 3 }));
    Check((await badIpa.Content.ReadAsStringAsync()).Contains("\"ok\":false"), "helper bad ipa");
    var devs = await http.GetStringAsync($"http://127.0.0.1:{hport}/devices");
    Check(devs.TrimStart().StartsWith("["), "helper devices list");
    var matRefresh = await http.PostAsync($"http://127.0.0.1:{hport}/refresh",
        new System.Net.Http.StringContent("{\"bundleID\":\"com.nope\",\"profile\":\"e30=\",\"certPem\":\"x\",\"keyPem\":\"y\"}", System.Text.Encoding.UTF8, "application/json"));
    Check((await matRefresh.Content.ReadAsStringAsync()).Contains("\"ok\":false"), "helper bad materials fail gracefully");
    var (rr, changed) = await HelperRefreshService.HandleAsync(
        new RefreshRequest(BundleId: "com.nope", Profile: "e30=", CertPem: "x", KeyPem: "y"),
        () => Task.FromResult(""), _ => { }, CancellationToken.None);
    Check(!rr.Ok && !changed, "refresh service unknown app");
}

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURES");
return failures;
