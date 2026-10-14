using System.Runtime.InteropServices;
using System.Text;

namespace FreightCopilot;

// Keeps secrets in Windows Credential Manager (Control Panel > Credential Manager > Windows Credentials),
// where Windows encrypts them for the signed-in user. Nothing secret is written to settings.json or a log.
public static class CredentialVault
{
    public const string UexAppToken = "FreightPlanner: UEX app token";
    public const string UexSecretKey = "FreightPlanner: UEX secret key";
    private const int Generic = 1;
    private const int PersistLocalMachine = 2;

    public static UexCredentials? Uex() => Read(UexAppToken) is { Length: > 0 } token && Read(UexSecretKey) is { Length: > 0 } key
        ? new UexCredentials(token, key) : null;

    public static string? Read(string name)
    {
        if (!CredRead(name, Generic, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.BlobSize > 0 ? Marshal.PtrToStringUni(credential.Blob, credential.BlobSize / 2) : "";
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string name, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = Generic, TargetName = name, BlobSize = bytes.Length, Blob = blob, Persist = PersistLocalMachine, UserName = "Freight Planner"
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException($"Windows Credential Manager refused the entry (error {Marshal.GetLastWin32Error()}).");
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public static void Delete(string name) => CredDelete(name, Generic, 0);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int BlobSize;
        public IntPtr Blob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
