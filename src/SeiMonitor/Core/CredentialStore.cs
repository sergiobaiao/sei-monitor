using System.Runtime.InteropServices;
using System.Text;

namespace SeiMonitor.Core;

/// <summary>
/// Gerenciador de armazenamento seguro de credenciais via Windows Credential Manager (Advapi32.dll).
/// Atende às diretrizes de segurança da Constituição do SEI Monitor (sem credenciais em logs ou SQLite).
/// </summary>
public static class CredentialStore
{
    public const string DefaultTarget = "SEI Monitor";

    private static readonly object _syncLock = new();
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_ENTERPRISE = 3;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr cred);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref CREDENTIAL cred, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr cred);

    /// <summary>
    /// Constrói o target padronizado para o usuário ou retorna o padrão da aplicação.
    /// </summary>
    public static string BuildTarget(string? username = null)
    {
        return string.IsNullOrWhiteSpace(username) ? DefaultTarget : $"{DefaultTarget}:{username}";
    }

    /// <summary>
    /// Salva de forma criptografada as credenciais no cofre nativo do Windows.
    /// </summary>
    public static bool SaveCredential(string target, string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        lock (_syncLock)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(password);
            int byteCount = bytes.Length;
            IntPtr blob = Marshal.AllocHGlobal(Math.Max(1, byteCount));

            try
            {
                Marshal.Copy(bytes, 0, blob, byteCount);

                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    UserName = username,
                    Comment = "Stored by SEI Monitor",
                    CredentialBlob = blob,
                    CredentialBlobSize = byteCount,
                    Persist = CRED_PERSIST_ENTERPRISE
                };

                if (!CredWriteW(ref cred, 0))
                {
                    return false;
                }

                return true;
            }
            finally
            {
                if (blob != IntPtr.Zero)
                {
                    byte[] zeroes = new byte[byteCount];
                    Marshal.Copy(zeroes, 0, blob, byteCount);
                    Marshal.FreeHGlobal(blob);
                }
                Array.Clear(bytes, 0, bytes.Length);
            }
        }
    }

    /// <summary>
    /// Carrega as credenciais salvas para o target informado.
    /// Retorna (null, null) caso não existam credenciais registradas.
    /// </summary>
    public static (string? username, string? password) LoadCredential(string target = DefaultTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        lock (_syncLock)
        {
            if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var ptr))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_FOUND)
                {
                    return (null, null);
                }
                return (null, null);
            }

            try
            {
                var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);

                if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize == 0)
                {
                    return (c.UserName ?? string.Empty, string.Empty);
                }

                var bytes = new byte[c.CredentialBlobSize];
                Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
                var password = Encoding.Unicode.GetString(bytes);
                Array.Clear(bytes, 0, bytes.Length);

                return (c.UserName ?? string.Empty, password);
            }
            finally
            {
                CredFree(ptr);
            }
        }
    }

    /// <summary>
    /// Remove as credenciais vinculadas ao target especificado.
    /// Operação idempotente: retorna true caso já não existam.
    /// </summary>
    public static bool DeleteCredential(string target = DefaultTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        lock (_syncLock)
        {
            if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_FOUND)
                {
                    return true; // Idempotente
                }
                return false;
            }

            return true;
        }
    }

    // Sobrecargas de conveniência
    public static bool Save(string username, string password) => SaveCredential(DefaultTarget, username, password);
    public static (string? username, string? password) Load() => LoadCredential(DefaultTarget);
    public static bool Delete() => DeleteCredential(DefaultTarget);
}
