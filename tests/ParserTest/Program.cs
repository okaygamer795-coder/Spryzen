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

Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURES");
return failures;
