using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InternalAssetLibrary.Client.Core.Auth;

public static class ApplicationCredentialPurger
{
    private const int ErrorNotFound = 1168;
    private const uint CredentialTypeGeneric = 1;

    public static void Purge()
    {
        if (OperatingSystem.IsWindows())
        {
            PurgeWindows();
        }
    }

    internal static bool IsManagedCredentialTarget(string? targetName)
    {
        if (string.IsNullOrEmpty(targetName))
        {
            return false;
        }

        var sessionPrefix = SessionTokenStoreFactory.DefaultCredentialTargetPrefix + ":v1:";
        if (targetName.StartsWith(sessionPrefix, StringComparison.Ordinal) &&
            IsHex(targetName.AsSpan(sessionPrefix.Length), 64))
        {
            return true;
        }

        var passwordPrefix = RememberedLoginPasswordStore.DefaultCredentialTargetPrefix + ".";
        if (!targetName.StartsWith(passwordPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = targetName.AsSpan(passwordPrefix.Length);
        return remainder.Length == 32 + 4 + 64 &&
               IsHex(remainder[..32], 32) &&
               remainder.Slice(32, 4).SequenceEqual(":v1:") &&
               IsHex(remainder[36..], 64);
    }

    [SupportedOSPlatform("windows")]
    private static void PurgeWindows()
    {
        var failures = new List<Exception>();
        DeleteMatching(
            SessionTokenStoreFactory.DefaultCredentialTargetPrefix + "*",
            failures);
        DeleteMatching(
            RememberedLoginPasswordStore.DefaultCredentialTargetPrefix + "*",
            failures);
        if (failures.Count > 0)
        {
            throw new AggregateException("Some application credentials could not be deleted.", failures);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteMatching(string filter, ICollection<Exception> failures)
    {
        if (!NativeMethods.CredEnumerate(filter, 0, out var count, out var credentialsPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                failures.Add(new Win32Exception(error, "Windows Credential Manager could not enumerate application credentials."));
            }

            return;
        }

        try
        {
            for (var index = 0u; index < count; index++)
            {
                var credentialPointer = Marshal.ReadIntPtr(credentialsPointer, checked((int)index * IntPtr.Size));
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                var targetName = Marshal.PtrToStringUni(credential.TargetName);
                if (!IsManagedCredentialTarget(targetName))
                {
                    continue;
                }

                if (!NativeMethods.CredDelete(targetName!, CredentialTypeGeneric, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorNotFound)
                    {
                        failures.Add(new Win32Exception(error, "Windows Credential Manager could not delete an application credential."));
                    }
                }
            }
        }
        finally
        {
            NativeMethods.CredFree(credentialsPointer);
        }
    }

    private static bool IsHex(ReadOnlySpan<char> value, int expectedLength)
    {
        if (value.Length != expectedLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredEnumerate(
            string filter,
            uint flags,
            out uint count,
            out IntPtr credentials);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}
