using System.Runtime.InteropServices;
using System.Text;

namespace Dictum.Services;

/// <summary>
/// Reads a generic credential from Windows Credential Manager, so the API key
/// does not have to sit in a plain-text config file.
/// </summary>
internal static class CredentialManager
{
    private const int CredTypeGeneric = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    public static string? Read(string target)    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            Log.Write($"credman: '{target}' not read, win32={Marshal.GetLastWin32Error()}");
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize <= 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

            // cmdkey may store the secret as UTF-16LE or UTF-8 depending on how
            // the entry was created. An API key is ASCII, so pick the decoding
            // that yields printable ASCII instead of guessing.
            var utf8 = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
            var utf16 = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();

            if (IsAscii(utf8))
            {
                return utf8;
            }

            if (IsAscii(utf16))
            {
                return utf16;
            }

            return utf8.Length <= utf16.Length ? utf8 : utf16;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static bool IsAscii(string value) =>
        value.Length > 0 && value.All(ch => ch >= 32 && ch < 127);
}
