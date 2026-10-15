using System.IO;
using System.Text;

namespace SideLoadr.Services;

/// <summary>Writes the subset of plist values we produce back out as XML plist.
/// Binary plists read by <see cref="PlistParser"/> are normalized to XML —
/// iOS accepts XML Info.plists fine.</summary>
public static class PlistWriter
{
    public static byte[] WriteXml(Dictionary<string, object?> dict)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
        sb.AppendLine(@"<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">");
        sb.AppendLine(@"<plist version=""1.0"">");
        WriteValue(sb, dict, 0);
        sb.AppendLine("</plist>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void WriteValue(StringBuilder sb, object? value, int indent)
    {
        string pad = new(' ', indent * 2);
        switch (value)
        {
            case null:
                break; // plists have no null; skip
            case Dictionary<string, object?> dict:
                sb.AppendLine(pad + "<dict>");
                foreach (var kv in dict)
                {
                    if (kv.Value is null)
                        continue;
                    sb.AppendLine(pad + $"  <key>{Escape(kv.Key)}</key>");
                    WriteValue(sb, kv.Value, indent + 1);
                }
                sb.AppendLine(pad + "</dict>");
                break;
            case List<object?> list:
                sb.AppendLine(pad + "<array>");
                foreach (var item in list)
                    WriteValue(sb, item, indent + 1);
                sb.AppendLine(pad + "</array>");
                break;
            case string s:
                sb.AppendLine(pad + $"<string>{Escape(s)}</string>");
                break;
            case bool b:
                sb.AppendLine(pad + (b ? "<true/>" : "<false/>"));
                break;
            case long l:
                sb.AppendLine(pad + $"<integer>{l}</integer>");
                break;
            case int i:
                sb.AppendLine(pad + $"<integer>{i}</integer>");
                break;
            case double d:
                sb.AppendLine(pad + $"<real>{d.ToString(System.Globalization.CultureInfo.InvariantCulture)}</real>");
                break;
            case float f:
                sb.AppendLine(pad + $"<real>{f.ToString(System.Globalization.CultureInfo.InvariantCulture)}</real>");
                break;
            case DateTime dt:
                sb.AppendLine(pad + $"<date>{dt.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}</date>");
                break;
            case DateTimeOffset dto:
                sb.AppendLine(pad + $"<date>{dto.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}</date>");
                break;
            case byte[] bytes:
                sb.AppendLine(pad + $"<data>{Convert.ToBase64String(bytes)}</data>");
                break;
            default:
                sb.AppendLine(pad + $"<string>{Escape(value.ToString() ?? "")}</string>");
                break;
        }
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
