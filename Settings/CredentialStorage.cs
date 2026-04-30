using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Thin wrapper around the Windows Credential Manager (advapi32.dll).
    /// Visual Studio uses this same store for its built-in credential management
    /// (Git auth, Live Share, package feeds, etc.), so storing here keeps secrets
    /// out of plaintext settings files and gets us OS-level DPAPI protection for free.
    /// </summary>
    internal static class CredentialStorage
    {
        private const string TargetName = "OllamaCopilot:Auth";
        private const int ERROR_NOT_FOUND = 1168;

        // ---- Public API ----

        public static string GetUsername()
        {
            var (user, _) = Read();
            return user;
        }

        public static string GetPassword()
        {
            var (_, pass) = Read();
            return pass;
        }

        public static (string Username, string Password) Read()
        {
            if (!CredRead(TargetName, CRED_TYPE.GENERIC, 0, out IntPtr credPtr))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_FOUND) return (null, null);
                return (null, null); // best-effort: never throw out of read
            }

            try
            {
                var cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL));
                string user = cred.UserName ?? string.Empty;
                string pass = (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
                    ? Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2))
                    : string.Empty;
                return (user, pass);
            }
            finally
            {
                CredFree(credPtr);
            }
        }

        public static void Save(string username, string password)
        {
            byte[] passBytes = Encoding.Unicode.GetBytes(password ?? string.Empty);
            IntPtr blobPtr = passBytes.Length > 0 ? Marshal.AllocHGlobal(passBytes.Length) : IntPtr.Zero;
            try
            {
                if (passBytes.Length > 0)
                    Marshal.Copy(passBytes, 0, blobPtr, passBytes.Length);

                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE.GENERIC,
                    TargetName = TargetName,
                    UserName = username ?? string.Empty,
                    CredentialBlob = blobPtr,
                    CredentialBlobSize = (uint)passBytes.Length,
                    Persist = CRED_PERSIST.LOCAL_MACHINE, // per-user, persists across reboots, no admin required
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    Comment = "Ollama Code Completions HTTP Basic auth",
                    TargetAlias = null,
                };

                if (!CredWrite(ref cred, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (blobPtr != IntPtr.Zero) Marshal.FreeHGlobal(blobPtr);
            }
        }

        public static void Delete()
        {
            CredDelete(TargetName, CRED_TYPE.GENERIC, 0);
        }

        // ---- P/Invoke ----

        private enum CRED_TYPE : uint { GENERIC = 1 }
        private enum CRED_PERSIST : uint { SESSION = 1, LOCAL_MACHINE = 2, ENTERPRISE = 3 }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public CRED_TYPE Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public CRED_PERSIST Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, CRED_TYPE type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite([In] ref CREDENTIAL credential, [In] uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, CRED_TYPE type, int flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CredFree([In] IntPtr cred);
    }
}
