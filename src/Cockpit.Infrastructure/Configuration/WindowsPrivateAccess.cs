using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Cockpit.Infrastructure.Configuration;

[SupportedOSPlatform("windows")]
internal static class WindowsPrivateAccess
{
    public static void Restrict(string path, bool directory, bool create = false)
    {
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User ?? throw new UnauthorizedAccessException("No Windows owner identity.");
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        IdentityReference? existingOwner = directory
            ? Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) : null
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        if (!owner.Equals(existingOwner))
        {
            security.SetOwner(owner);
        }
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow));
        FileSystemSecurity actual;
        if (directory)
        {
            var info = new DirectoryInfo(path);
            if (create)
            {
                info.Create((DirectorySecurity)security);
            }

            info.SetAccessControl((DirectorySecurity)security);
            actual = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        }
        else
        {
            var info = new FileInfo(path);
            info.SetAccessControl((FileSecurity)security);
            actual = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        }

        AuthorizationRuleCollection rules = actual.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        if (!actual.AreAccessRulesProtected || !owner.Equals(actual.GetOwner(typeof(SecurityIdentifier))) || rules.Count != 1
            || rules[0] is not FileSystemAccessRule rule || rule.IsInherited || !owner.Equals(rule.IdentityReference)
            || rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights != FileSystemRights.FullControl)
        {
            throw new UnauthorizedAccessException("Private storage ACL verification failed.");
        }
    }
}
