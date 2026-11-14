using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Plugin.Browser.Ipc;

internal static class BrowserIpcStorage
{
    internal static string Prepare(string trustedParent)
    {
        trustedParent = ValidateAncestors(trustedParent);
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
        return ValidateDirectory(trustedParent);
    }

    internal static string PrepareCache(string path)
    {
        path = _ResolveDirectory(path);
        ValidateAncestors(Path.GetDirectoryName(path) ?? path);
        if (Directory.Exists(path) == false)
        {
            return Prepare(path);
        }
        if (OperatingSystem.IsWindows())
        {
            _ValidateWindows(new DirectoryInfo(path).GetAccessControl(), cache: true);
        }
        else if (OperatingSystem.IsLinux())
        {
            var status = _LinuxStatus(path, null);
            if ((status.Mask & 0xf) != 0xf || status.Owner != _GetEffectiveUserId() || (status.Mode & 0xf000) != 0x4000)
            {
                throw new UnauthorizedAccessException("The browser cache belongs to another owner.");
            }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            throw new PlatformNotSupportedException("IPC storage is supported on Windows and Linux.");
        }
        return path;
    }

    private static string _ResolveDirectory(string path)
    {
        if (Path.IsPathFullyQualified(path) == false)
        {
            throw new UnauthorizedAccessException("IPC storage needs an absolute local path.");
        }
        for (int links = 0; links < 40; links++)
        {
            path = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows() && path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            {
                path = path[4..];
            }
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || Path.IsPathFullyQualified(path) == false)
            {
                throw new UnauthorizedAccessException("IPC storage needs an absolute local path.");
            }
            string root = Path.GetPathRoot(path) ?? throw new UnauthorizedAccessException("IPC storage has no filesystem root.");
            string resolved = root;
            var components = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            bool followed = false;
            for (int index = 0; index < components.Length; index++)
            {
                resolved = Path.Combine(resolved, components[index]);
                var directory = new DirectoryInfo(resolved);
                if (directory.LinkTarget is not null)
                {
                    path = directory.ResolveLinkTarget(false)?.FullName ?? throw new UnauthorizedAccessException("IPC storage has an unresolved link.");
                    foreach (string component in components.Skip(index + 1))
                    {
                        path = Path.Combine(path, component);
                    }
                    followed = true;
                    break;
                }
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("IPC storage has an unsupported reparse point.");
                }
            }
            if (followed == false)
            {
                return Path.TrimEndingDirectorySeparator(resolved);
            }
        }
        throw new UnauthorizedAccessException("IPC storage has a link cycle.");
    }

    internal static string ValidateAncestors(string path)
    {
        path = _ResolveDirectory(path);
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null || (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new UnauthorizedAccessException("IPC storage cannot traverse links or reparse points.");
            }
            if (directory.Exists && OperatingSystem.IsLinux())
            {
                var status = _LinuxStatus(directory.FullName, null);
                bool sticky = (status.Mode & 0x200) != 0;
                if ((status.Mask & 0xf) != 0xf || (status.Owner != _GetEffectiveUserId() && status.Owner != 0) || ((status.Mode & 0x12) != 0 && sticky == false))
                {
                    throw new UnauthorizedAccessException("IPC storage has an untrusted ancestor.");
                }
            }
            if (directory.Exists && OperatingSystem.IsWindows())
            {
                _ValidateWindowsAncestor(directory);
            }
        }
        return path;
    }

    internal static string ValidateDirectory(string path)
    {
        path = ValidateAncestors(path);
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
        return path;
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
    private static SecurityIdentifier[] _TrustedWindowsPrincipals()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return
        [
            identity.User ?? throw new UnauthorizedAccessException("IPC storage owner is unavailable."),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier)),
        ];
    }

    [SupportedOSPlatform("windows")]
    private static void _ValidateWindowsAncestor(DirectoryInfo directory)
    {
        var trusted = _TrustedWindowsPrincipals();
        bool Trusted(IdentityReference? principal) => trusted.Any(identity => identity.Equals(principal));
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
            var forbidden = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if (directory.Parent is not null)
            {
                forbidden |= FileSystemRights.Delete;
            }
            if ((rule.FileSystemRights & forbidden) != 0)
            {
                throw new UnauthorizedAccessException("IPC storage has a replaceable ancestor.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void _ValidateWindows(FileSystemSecurity security, bool cache = false)
    {
        var principals = _TrustedWindowsPrincipals();
        var trusted = cache ? principals : [principals[0]];
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            throw new UnauthorizedAccessException("IPC storage has no restrictive access control list.");
        }
        if (trusted.Any(principal => principal.Equals(security.GetOwner(typeof(SecurityIdentifier)))) == false)
        {
            throw new UnauthorizedAccessException("IPC storage belongs to another owner.");
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights != 0 && trusted.Any(principal => principal.Equals(rule.IdentityReference)) == false)
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
