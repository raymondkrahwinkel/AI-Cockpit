using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Cockpit.Infrastructure.Updates;

// Only SYSTEM, Administrators (deny-only in a medium-IL token) and TrustedInstaller may own or change an entry.
[SupportedOSPlatform("windows")]
internal static class WindowsInstallAcl
{
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    private const int AnyChange = GenericAll | GenericWrite
        | (int)(FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);

    // A parent may allow creating siblings (C:\ does), never moving, deleting or re-ACLing the child.
    private const int AncestorTakeover = GenericAll
        | (int)(FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);

    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),
    ];

    public static void Check(string path, string name, bool ancestor, List<string> failures)
    {
        FileSystemSecurity security;
        try
        {
            security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            failures.Add($"unreadable:{name}");

            return;
        }

        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner))
        {
            failures.Add($"untrusted-owner:{name}");
        }

        var forbidden = ancestor ? AncestorTakeover : AnyChange;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                && ((int)rule.FileSystemRights & forbidden) != 0
                && !Trusted.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                failures.Add($"untrusted-write-access:{name}");

                return;
            }
        }
    }
}

// WinVerifyTrust for the file's own embedded signature, then the leaf signer's SHA-256 thumbprint for pinning.
[SupportedOSPlatform("windows")]
internal static class AuthenticodeSignature
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static string? VerifiedSignerSha256(string path)
    {
        var fileInfo = new WintrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WintrustFileInfo>(), FilePath = path,
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            // ponytail: no revocation lookup so an offline VM can verify; the publisher pin is the trust anchor.
            var data = new WintrustData
            {
                Size = (uint)Marshal.SizeOf<WintrustData>(), UiChoice = 2, UnionChoice = 1, File = fileInfoPointer,
                StateAction = 1, ProviderFlags = 0x10,
            };
            var action = GenericVerifyV2;
            try
            {
                if (WinVerifyTrust(-1, ref action, ref data) != 0)
                {
                    return null;
                }

                // The leaf of the chain WinVerifyTrust itself built, so the pin is read from what was verified.
                var signer = WTHelperGetProvSignerFromChain(WTHelperProvDataFromStateData(data.StateData), 0, false, 0);
                var leaf = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
                if (leaf == 0)
                {
                    return null;
                }

                using var certificate = new X509Certificate2(Marshal.ReadIntPtr(leaf, IntPtr.Size));

                return certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
            }
            finally
            {
                data.StateAction = 2;
                WinVerifyTrust(-1, ref action, ref data);
            }
        }
        finally
        {
            Marshal.DestroyStructure<WintrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint Size;
        public string FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll")]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DllImport("wintrust.dll")]
    private static extern nint WTHelperGetProvSignerFromChain(nint providerData, uint signerIndex, bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll")]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);
}
