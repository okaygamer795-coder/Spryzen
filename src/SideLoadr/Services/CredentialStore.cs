using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SideLoadr.Services;

/// <summary>Stores the Apple ID password in Windows Credential Manager
/// (DPAPI-backed) for "remember me" + headless auto-refresh. Never in files.</summary>
public static class CredentialStore
{
    public const string DefaultTarget = "SideLoadr/AppleId";

    private const int CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL cred, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr cred);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr cred);

    public static void Save(string appleId, string password, string target = DefaultTarget)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password);
        IntPtr blobPtr = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = appleId,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException("Could not save credentials (Win32 error " + Marshal.GetLastWin32Error() + ").");
        }
        finally
        {
            Array.Clear(blob, 0, blob.Length);
            Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    public static bool TryLoad(out string appleId, out string password, string target = DefaultTarget)
    {
        appleId = "";
        password = "";
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr ptr) || ptr == IntPtr.Zero)
            return false;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            appleId = cred.UserName ?? "";
            if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
                password = Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2) ?? "";
            return password.Length > 0;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static void Delete(string target = DefaultTarget)
    {
        CredDelete(target, CRED_TYPE_GENERIC, 0);
    }
}
