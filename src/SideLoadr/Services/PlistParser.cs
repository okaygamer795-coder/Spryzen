using System.IO;
using System.Text;
using System.Xml;

namespace SideLoadr.Services;

/// <summary>Dependency-free plist reader (XML + binary/bplist). Returns nested
/// Dictionary&lt;string, object?&gt; / List&lt;object?&gt; / string / long / double / bool / DateTime / byte[].</summary>
public static class PlistParser
{
    private static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static Dictionary<string, object?> Parse(byte[] bytes)
    {
        object? root = bytes.Length > 8 && Encoding.ASCII.GetString(bytes, 0, 8) == "bplist00"
            ? ParseBinary(bytes)
            : ParseXml(bytes);
        if (root is Dictionary<string, object?> dict)
            return dict;
        throw new InvalidDataException("Plist top-level object is not a dictionary.");
    }

    // ---------------- XML ----------------

    private static object? ParseXml(byte[] bytes)
    {
        var doc = new XmlDocument();
        using var ms = new MemoryStream(bytes, writable: false);
        doc.Load(ms);
        var plist = doc.DocumentElement ?? throw new InvalidDataException("Invalid XML plist.");
        foreach (XmlNode node in plist.ChildNodes)
            if (node is XmlElement el)
                return ConvertXml(el);
        throw new InvalidDataException("Empty XML plist.");
    }

    private static object? ConvertXml(XmlElement el) => el.Name switch
    {
        "dict" => ParseXmlDict(el),
        "array" => ParseXmlArray(el),
        "string" => el.InnerText,
        "integer" => long.TryParse(el.InnerText.Trim(), out var l) ? l : el.InnerText,
        "real" => double.TryParse(el.InnerText.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : el.InnerText,
        "true" => true,
        "false" => false,
        "date" => XmlConvert.ToDateTime(el.InnerText.Trim(), XmlDateTimeSerializationMode.Utc),
        "data" => Convert.FromBase64String(el.InnerText.Trim()),
        _ => el.InnerText,
    };

    private static Dictionary<string, object?> ParseXmlDict(XmlElement dict)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? key = null;
        foreach (XmlNode node in dict.ChildNodes)
        {
            if (node is not XmlElement el)
                continue;
            if (el.Name == "key")
                key = el.InnerText;
            else if (key is not null)
            {
                result[key] = ConvertXml(el);
                key = null;
            }
        }
        return result;
    }

    private static List<object?> ParseXmlArray(XmlElement arr)
    {
        var list = new List<object?>();
        foreach (XmlNode node in arr.ChildNodes)
            if (node is XmlElement el)
                list.Add(ConvertXml(el));
        return list;
    }

    // ---------------- Binary (bplist) ----------------

    private static object? ParseBinary(byte[] b)
    {
        if (b.Length < 40)
            throw new InvalidDataException("Truncated binary plist.");
        int n = b.Length;
        int offsetSize = b[n - 26];
        int refSize = b[n - 25];
        ulong numObjects = ReadUIntBE(b, n - 24, 8);
        ulong topIndex = ReadUIntBE(b, n - 16, 8);
        ulong tableOffset = ReadUIntBE(b, n - 8, 8);
        if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8)
            throw new InvalidDataException("Bad bplist trailer.");
        if (numObjects > int.MaxValue || numObjects == 0 || tableOffset > (ulong)n)
            throw new InvalidDataException("Bad bplist trailer.");

        var reader = new BinaryReader(b, (int)numObjects, offsetSize, refSize, tableOffset);
        return reader.ReadRoot((int)topIndex);
    }

    private static ulong ReadUIntBE(byte[] b, long offset, int len)
    {
        if (len is < 1 or > 8 || offset < 0 || offset + len > b.Length)
            throw new InvalidDataException("Bad bplist integer.");
        ulong v = 0;
        for (int i = 0; i < len; i++)
            v = (v << 8) | b[offset + i];
        return v;
    }

    private sealed class BinaryReader
    {
        private readonly byte[] _b;
        private readonly int _numObjects;
        private readonly int _refSize;
        private readonly long[] _offsets;
        private readonly object?[] _cache;
        private readonly bool[] _done;
        private readonly bool[] _busy;

        public BinaryReader(byte[] b, int numObjects, int offsetSize, int refSize, ulong tableOffset)
        {
            _b = b;
            _numObjects = numObjects;
            _refSize = refSize;
            _offsets = new long[numObjects];
            for (int i = 0; i < numObjects; i++)
                _offsets[i] = (long)ReadUIntBE(b, (long)tableOffset + (long)i * offsetSize, offsetSize);
            _cache = new object?[numObjects];
            _done = new bool[numObjects];
            _busy = new bool[numObjects];
        }

        public object? ReadRoot(int topIndex)
        {
            if (topIndex < 0 || topIndex >= _numObjects)
                throw new InvalidDataException("Bad bplist top object.");
            return ReadObj(topIndex);
        }

        private object? ReadObj(int index)
        {
            if (_done[index])
                return _cache[index];
            if (_busy[index])
                throw new InvalidDataException("Cyclic bplist.");
            _busy[index] = true;

            long off = _offsets[index];
            if (off < 0 || off >= _b.Length)
                throw new InvalidDataException("Bad bplist object offset.");
            byte marker = _b[off++];
            int type = marker >> 4;
            int info = marker & 0x0F;

            object? value = type switch
            {
                0x0 => info switch
                {
                    0x00 => null,
                    0x08 => false,
                    0x09 => true,
                    _ => throw new InvalidDataException("Bad bplist primitive."),
                },
                0x1 => (long)ReadUIntBE(_b, off, 1 << info),
                0x2 => ReadReal(ref off, 1 << info),
                0x3 => AppleEpoch.AddSeconds(ReadDoubleBE(ref off)),
                0x4 => ReadBytes(ref off, GetLength(ref off, info)),
                0x5 => Encoding.ASCII.GetString(ReadBytes(ref off, GetLength(ref off, info))),
                0x6 => Encoding.BigEndianUnicode.GetString(ReadBytes(ref off, GetLength(ref off, info) * 2)),
                0x8 => (long)ReadUIntBE(_b, off, info + 1),
                0xA or 0xC => ReadArray(ref off, info),
                0xD => ReadDict(ref off, info),
                _ => throw new InvalidDataException($"Unsupported bplist type 0x{type:X}."),
            };

            _cache[index] = value;
            _done[index] = true;
            _busy[index] = false;
            return value;
        }

        private int GetLength(ref long off, int info)
        {
            if (info != 0x0F)
                return info;
            if (off >= _b.Length)
                throw new InvalidDataException("Truncated bplist length.");
            byte m = _b[off++];
            if ((m >> 4) != 0x1)
                throw new InvalidDataException("Bad bplist length prefix.");
            int len = 1 << (m & 0x0F);
            int value = checked((int)ReadUIntBE(_b, off, len));
            off += len;
            return value;
        }

        private double ReadReal(ref long off, int len)
        {
            byte[] raw = ReadBytes(ref off, len);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(raw);
            return len switch
            {
                4 => BitConverter.ToSingle(raw, 0),
                8 => BitConverter.ToDouble(raw, 0),
                _ => throw new InvalidDataException("Bad bplist real."),
            };
        }

        private double ReadDoubleBE(ref long off)
        {
            byte[] raw = ReadBytes(ref off, 8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(raw);
            return BitConverter.ToDouble(raw, 0);
        }

        private byte[] ReadBytes(ref long off, int len)
        {
            if (len < 0 || off < 0 || off + len > _b.Length)
                throw new InvalidDataException("Truncated bplist.");
            var buf = new byte[len];
            Buffer.BlockCopy(_b, (int)off, buf, 0, len);
            off += len;
            return buf;
        }

        private int ReadRef(ref long off)
        {
            if (_refSize is < 1 or > 8)
                throw new InvalidDataException("Bad bplist ref size.");
            int idx = checked((int)ReadUIntBE(_b, off, _refSize));
            off += _refSize;
            if (idx < 0 || idx >= _numObjects)
                throw new InvalidDataException("Bad bplist reference.");
            return idx;
        }

        private List<object?> ReadArray(ref long off, int info)
        {
            int count = GetLength(ref off, info);
            var list = new List<object?>(count);
            for (int i = 0; i < count; i++)
                list.Add(ReadObj(ReadRef(ref off)));
            return list;
        }

        private Dictionary<string, object?> ReadDict(ref long off, int info)
        {
            int count = GetLength(ref off, info);
            var keys = new int[count];
            var vals = new int[count];
            for (int i = 0; i < count; i++)
                keys[i] = ReadRef(ref off);
            for (int i = 0; i < count; i++)
                vals[i] = ReadRef(ref off);
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
                dict[ReadObj(keys[i])?.ToString() ?? ""] = ReadObj(vals[i]);
            return dict;
        }
    }
}
