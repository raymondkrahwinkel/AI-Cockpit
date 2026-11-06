using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Plugin.Browser.Ipc;

internal static class BrowserIpcStorage
{
    internal static void Prepare(string trustedParent)
    {
        ValidateAncestors(trustedParent);
        if (Directory.Exists(trustedParent) == false)
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var owner = identity.User ?? throw new UnauthorizedAccessException("IPC storage owner is unavailable.");
                var security = new DirectorySecurity();
                security.SetOwner(owner);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(trustedParent).Create(security);
            }
            else
            {
                Directory.CreateDirectory(trustedParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        ValidateDirectory(trustedParent);
    }

    internal static void ValidateAncestors(string path)
    {
        if (Path.IsPathFullyQualified(path) == false || Path.GetFullPath(path) != Path.TrimEndingDirectorySeparator(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("IPC storage needs a canonical absolute path.");
        }
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new UnauthorizedAccessException("IPC storage cannot traverse links or reparse points.");
            }
            if (directory.Exists && OperatingSystem.IsLinux())
            {
                var status = _LinuxStatus(directory.FullName, null);
                if ((status.Mask & 0xf) != 0xf || (status.Owner != _GetEffectiveUserId() && status.Owner != 0) || (status.Mode & 0x12) != 0)
                {
                    throw new UnauthorizedAccessException("IPC storage has an untrusted ancestor.");
                }
            }
            if (directory.Exists && OperatingSystem.IsWindows())
            {
                _ValidateWindowsAncestor(directory);
            }
        }
    }

    internal static void ValidateDirectory(string path)
    {
        ValidateAncestors(path);
        if (OperatingSystem.IsWindows())
        {
            _ValidateWindows(new DirectoryInfo(path).GetAccessControl());
        }
        else if (OperatingSystem.IsLinux())
        {
            _ValidateLinux(_LinuxStatus(path, null), directory: true);
        }
        else
        {
            throw new PlatformNotSupportedException("IPC storage is supported on Windows and Linux.");
        }
    }

    internal static void ValidateFile(string path, FileStream file)
    {
        if (new FileInfo(path).LinkTarget is not null || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("IPC buffers cannot be links or reparse points.");
        }
        if (OperatingSystem.IsWindows())
        {
            _ValidateWindows(file.GetAccessControl());
        }
        else if (OperatingSystem.IsLinux())
        {
            _ValidateLinux(_LinuxStatus(string.Empty, file.SafeFileHandle), directory: false);
        }
        else
        {
            throw new PlatformNotSupportedException("IPC storage is supported on Windows and Linux.");
        }
    }

    internal static void ValidateExistingFile(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("IPC buffers cannot be links or reparse points.");
        }
        if (OperatingSystem.IsWindows())
        {
            _ValidateWindows(new FileInfo(path).GetAccessControl());
        }
        else if (OperatingSystem.IsLinux())
        {
            _ValidateLinux(_LinuxStatus(path, null), directory: false);
        }
        else
        {
            throw new PlatformNotSupportedException("IPC storage is supported on Windows and Linux.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void _ValidateWindowsAncestor(DirectoryInfo directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException("IPC storage owner is unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var installer = new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier));
        bool Trusted(IdentityReference? principal) => owner.Equals(principal) || system.Equals(principal) || administrators.Equals(principal) || installer.Equals(principal);
        var security = directory.GetAccessControl();
        if (Trusted(security.GetOwner(typeof(SecurityIdentifier))) == false)
        {
            throw new UnauthorizedAccessException("IPC storage has an ancestor owned by another principal.");
        }
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
        {
            throw new UnauthorizedAccessException("IPC storage has an unrestricted ancestor.");
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 || rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }
            var principal = rule.IdentityReference;
            if (Trusted(principal))
            {
                continue;
            }
            if ((rule.FileSystemRights & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
            {
                throw new UnauthorizedAccessException("IPC storage has a replaceable ancestor.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void _ValidateWindows(FileSystemSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException("IPC storage owner is unavailable.");
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            throw new UnauthorizedAccessException("IPC storage has no restrictive access control list.");
        }
        if (owner.Equals(security.GetOwner(typeof(SecurityIdentifier))) == false)
        {
            throw new UnauthorizedAccessException("IPC storage belongs to another owner.");
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights != 0 && owner.Equals(rule.IdentityReference) == false)
            {
                throw new UnauthorizedAccessException("IPC storage is accessible to another principal.");
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static void _ValidateLinux(LinuxFileStatus status, bool directory)
    {
        int expected = directory ? 0x41c0 : 0x8180;
        if ((status.Mask & 0xf) != 0xf || status.Owner != _GetEffectiveUserId() || status.Mode != expected || (directory == false && status.Links != 1))
        {
            throw new UnauthorizedAccessException("IPC storage must be owned exclusively by the current user.");
        }
    }

    [SupportedOSPlatform("linux")]
    private static LinuxFileStatus _LinuxStatus(string path, SafeFileHandle? handle)
    {
        bool retained = false;
        try
        {
            handle?.DangerousAddRef(ref retained);
            int descriptor = handle is null ? -100 : handle.DangerousGetHandle().ToInt32();
            if (_Statx(descriptor, path, handle is null ? 0x100 : 0x1000, 0x7ff, out var status) != 0)
            {
                throw new IOException("Could not inspect IPC storage ownership.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            return status;
        }
        finally
        {
            if (retained)
            {
                handle?.DangerousRelease();
            }
        }
    }

    // statx has the same ABI on Linux x64 and arm64; unlike struct stat it does not depend on libc layout.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(20)] internal uint Owner;
        [FieldOffset(28)] internal ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int _Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxFileStatus status);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint _GetEffectiveUserId();
}
