using System.IO;
using System.Text;

namespace SideLoadr.Services;

/// <summary>Minimal Mach-O editor: inserts an LC_LOAD_DYLIB command so a .dylib
/// in Frameworks gets loaded at launch (tweak injection). Supports thin arm64
/// and fat binaries. Refuses App Store-encrypted binaries (cryptid != 0 —
/// those need decrypting on a jailbroken device first, which this PC can't do).
/// The binary must be re-signed afterwards (zsign step); injection alone
/// invalidates the existing code signature by design.</summary>
public static class MachOInjector
{
    private const uint MH_MAGIC_64 = 0xfeedfacf;
    private const uint FAT_MAGIC_READ_LE = 0xbebafeca; // 0xcafebabe bytes, big-endian file
    private const uint LC_SEGMENT_64 = 0x19;
    private const uint LC_LOAD_DYLIB = 0xC;
    private const uint LC_ENCRYPTION_INFO = 0x21;
    private const uint LC_ENCRYPTION_INFO_64 = 0x2C;

    public sealed record InjectResult(int SlicesPatched, int CommandSize, bool AlreadyPresent);

    public static InjectResult InjectIntoFile(string binaryPath, string dylibLoadPath)
    {
        byte[] data = File.ReadAllBytes(binaryPath);
        var result = InjectIntoBytes(data, dylibLoadPath);
        if (!result.AlreadyPresent)
            File.WriteAllBytes(binaryPath, data);
        return result;
    }

    public static InjectResult InjectIntoBytes(byte[] data, string dylibLoadPath)
    {
        if (string.IsNullOrWhiteSpace(dylibLoadPath))
            throw new ArgumentException("Empty dylib load path.", nameof(dylibLoadPath));
        if (data.Length < 32)
            throw new InvalidDataException("File too small to be a Mach-O binary.");

        uint magic = ReadU32LE(data, 0);
        if (magic == MH_MAGIC_64)
        {
            var (changed, cmdSize, already) = InjectThin(data, 0, data.Length, dylibLoadPath);
            return new InjectResult(changed ? 1 : 0, cmdSize, already);
        }
        if (magic == FAT_MAGIC_READ_LE)
        {
            uint nfat = ReadU32BE(data, 4);
            if (nfat == 0 || nfat > 8)
                throw new InvalidDataException("Bad fat binary header.");
            int patched = 0;
            int cmdSize = 0;
            bool allPresent = true;
            for (uint i = 0; i < nfat; i++)
            {
                int archOff = 8 + (int)i * 20;
                if (archOff + 20 > data.Length)
                    throw new InvalidDataException("Truncated fat_arch table.");
                int sliceOff = checked((int)ReadU32BE(data, archOff + 8));
                int sliceSize = checked((int)ReadU32BE(data, archOff + 12));
                if (sliceOff < 0 || sliceSize <= 0 || sliceOff + sliceSize > data.Length)
                    throw new InvalidDataException("Bad fat slice bounds.");
                if (ReadU32LE(data, sliceOff) != MH_MAGIC_64)
                    throw new InvalidDataException("Fat slice is not a 64-bit Mach-O (only arm64 supported).");
                var (changed, size, already) = InjectThin(data, sliceOff, sliceSize, dylibLoadPath);
                if (changed)
                {
                    patched++;
                    cmdSize = size;
                }
                if (!already)
                    allPresent = false;
            }
            if (patched == 0 && allPresent)
                return new InjectResult(0, 0, true);
            return new InjectResult(patched, cmdSize, false);
        }
        throw new InvalidDataException("Not a 64-bit Mach-O or fat binary (only arm64 supported).");
    }

    /// <returns>(changed, commandSize, alreadyPresent)</returns>
    private static (bool Changed, int CommandSize, bool AlreadyPresent) InjectThin(
        byte[] data, int baseOff, int sliceLen, string dylibLoadPath)
    {
        if (baseOff + 32 > data.Length)
            throw new InvalidDataException("Truncated Mach-O header.");
        uint ncmds = ReadU32LE(data, baseOff + 16);
        uint sizeofcmds = ReadU32LE(data, baseOff + 20);
        if (ncmds > 200 || baseOff + 32 + sizeofcmds > data.Length)
            throw new InvalidDataException("Bad Mach-O load command table.");

        long headerEnd = baseOff + 32 + sizeofcmds;
        long minFileOff = sliceLen; // relative to slice start
        int cmdOff = baseOff + 32;
        for (uint i = 0; i < ncmds; i++)
        {
            if (cmdOff + 8 > data.Length)
                throw new InvalidDataException("Truncated load command.");
            uint cmd = ReadU32LE(data, cmdOff);
            uint cmdsize = ReadU32LE(data, cmdOff + 4);
            if (cmdsize < 8 || cmdOff + cmdsize > data.Length)
                throw new InvalidDataException("Bad load command size.");
            if (cmd == LC_SEGMENT_64)
            {
                if (cmdsize < 72)
                    throw new InvalidDataException("Bad LC_SEGMENT_64.");
                ulong fileoff = ReadU64LE(data, cmdOff + 40);
                ulong filesize = ReadU64LE(data, cmdOff + 48);
                if (filesize > 0 && (long)fileoff < minFileOff)
                    minFileOff = (long)fileoff;
            }
            else if (cmd == LC_ENCRYPTION_INFO || cmd == LC_ENCRYPTION_INFO_64)
            {
                uint cryptid = ReadU32LE(data, cmdOff + 16);
                if (cryptid != 0)
                    throw new InvalidDataException(
                        "Binary is App Store-encrypted (cryptid != 0). Decrypt it on a jailbroken device first — this PC cannot do that.");
            }
            else if (cmd == LC_LOAD_DYLIB)
            {
                uint nameOff = ReadU32LE(data, cmdOff + 8);
                string name = ReadCString(data, cmdOff + (int)nameOff, (int)cmdsize - (int)nameOff);
                if (name == dylibLoadPath)
                    return (false, 0, true);
            }
            cmdOff += (int)cmdsize;
        }

        byte[] nameBytes = Encoding.UTF8.GetBytes(dylibLoadPath);
        int cmdSize = Align8(24 + nameBytes.Length + 1);
        long headerEndAbs = headerEnd;
        long minFileOffAbs = baseOff + minFileOff;
        if (headerEndAbs + cmdSize > minFileOffAbs)
            throw new InvalidDataException(
                $"No room in the Mach-O header ({minFileOffAbs - headerEndAbs} bytes free, need {cmdSize}). " +
                "This binary can't take an injected load command.");
        if (headerEndAbs + cmdSize > data.Length)
            throw new InvalidDataException("Mach-O header extension would run past end of file.");

        int w = (int)headerEndAbs;
        WriteU32LE(data, w, LC_LOAD_DYLIB);
        WriteU32LE(data, w + 4, (uint)cmdSize);
        WriteU32LE(data, w + 8, 24); // name offset
        WriteU32LE(data, w + 12, 2); // timestamp
        WriteU32LE(data, w + 16, 0); // current_version
        WriteU32LE(data, w + 20, 0); // compatibility_version
        Buffer.BlockCopy(nameBytes, 0, data, w + 24, nameBytes.Length);
        data[w + 24 + nameBytes.Length] = 0;
        WriteU32LE(data, baseOff + 16, ncmds + 1);
        WriteU32LE(data, baseOff + 20, sizeofcmds + (uint)cmdSize);
        return (true, cmdSize, false);
    }

    private static int Align8(int n) => (n + 7) & ~7;

    private static uint ReadU32LE(byte[] b, long off) => BitConverter.ToUInt32(b, (int)off);

    private static ulong ReadU64LE(byte[] b, long off) => BitConverter.ToUInt64(b, (int)off);

    private static uint ReadU32BE(byte[] b, long off)
        => ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];

    private static void WriteU32LE(byte[] b, long off, uint v)
    {
        byte[] tmp = BitConverter.GetBytes(v);
        Buffer.BlockCopy(tmp, 0, b, (int)off, 4);
    }

    private static string ReadCString(byte[] b, int off, int maxLen)
    {
        if (off < 0 || maxLen <= 0 || off >= b.Length)
            return "";
        int len = 0;
        while (len < maxLen && off + len < b.Length && b[off + len] != 0)
            len++;
        return Encoding.UTF8.GetString(b, off, len);
    }
}
