using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace InternalAssetLibrary.Client.Core.Auth;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialSessionTokenStore : ISessionTokenStore
{
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 5 * 512;
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _targetPrefix;

    public WindowsCredentialSessionTokenStore(
        string targetPrefix = SessionTokenStoreFactory.DefaultCredentialTargetPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPrefix);
        var normalizedPrefix = targetPrefix.Trim();
        if (normalizedPrefix.Length > 512 || normalizedPrefix.Contains('\0'))
        {
            throw new ArgumentException("The credential target prefix is invalid.", nameof(targetPrefix));
        }

        _targetPrefix = normalizedPrefix;
    }

    public SessionTokenStorageScope StorageScope => SessionTokenStorageScope.OperatingSystemUserVault;

    public ValueTask<string?> GetAsync(
        Uri serverOrigin,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetName = CreateTargetName(ServerOrigin.Normalize(serverOrigin));
        if (!NativeMethods.CredRead(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return ValueTask.FromResult<string?>(null);
            }

            throw CreateCredentialException(error, "read");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                throw new InvalidDataException("The stored session credential is empty.");
            }

            if (credential.CredentialBlobSize > MaximumCredentialBlobSize)
            {
                throw new InvalidDataException("The stored session credential is too large.");
            }

            var tokenBytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, tokenBytes, 0, tokenBytes.Length);
                return ValueTask.FromResult<string?>(StrictUtf8.GetString(tokenBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
            }
        }
        finally
        {
            NativeMethods.CredFree(credentialPointer);
        }
    }

    public ValueTask SaveAsync(
        Uri serverOrigin,
        string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var origin = ServerOrigin.Normalize(serverOrigin);
        var tokenBytes = StrictUtf8.GetBytes(token);
        if (tokenBytes.Length > MaximumCredentialBlobSize)
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            throw new ArgumentException(
                $"The UTF-8 session token must not exceed {MaximumCredentialBlobSize} bytes.",
                nameof(token));
        }

        var blobPointer = Marshal.AllocHGlobal(tokenBytes.Length);
        try
        {
            Marshal.Copy(tokenBytes, 0, blobPointer, tokenBytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = CreateTargetName(origin),
                CredentialBlobSize = tokenBytes.Length,
                CredentialBlob = blobPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = origin
            };

            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw CreateCredentialException(Marshal.GetLastWin32Error(), "write");
            }

            return ValueTask.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            ZeroUnmanagedMemory(blobPointer, tokenBytes.Length);
            Marshal.FreeHGlobal(blobPointer);
        }
    }

    public ValueTask DeleteAsync(
        Uri serverOrigin,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetName = CreateTargetName(ServerOrigin.Normalize(serverOrigin));
        if (!NativeMethods.CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw CreateCredentialException(error, "delete");
            }
        }

        return ValueTask.CompletedTask;
    }

    private string CreateTargetName(string origin)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(origin));
        return $"{_targetPrefix}:v1:{Convert.ToHexString(hash)}";
    }

    private static Win32Exception CreateCredentialException(int error, string operation) =>
        new(error, $"Windows Credential Manager could not {operation} the session credential.");

    private static void ZeroUnmanagedMemory(IntPtr pointer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(
            string target,
            uint type,
            uint reservedFlag,
            out IntPtr credentialPointer);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}
