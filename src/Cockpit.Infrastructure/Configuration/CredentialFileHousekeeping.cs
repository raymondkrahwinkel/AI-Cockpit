using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Puts the cockpit's credential-bearing files in order at startup: restricts the ones an earlier version wrote
/// at the umask's permissions, and deletes the <c>--mcp-config</c> files an earlier version left behind.
/// <para>
/// This runs from <c>Program.Main</c> rather than from the constructor of whatever happens to touch these files,
/// because both jobs must happen on every start whether or not anything triggers them. A container singleton is
/// built lazily — an operator who opens no TTY session would never construct the launcher, and the stale token
/// in the temp directory would simply stay there.
/// </para>
/// </summary>
public static class CredentialFileHousekeeping
{
    public static void Run()
    {
        try
        {
            CockpitConfigPath.EnsurePrivateDirectory(CockpitConfigPath.Root);
            CockpitConfigPath.RestrictExistingFile(CockpitConfigPath.Default);
            CockpitConfigPath.RestrictExistingFile(Path.Combine(CockpitConfigPath.Root, "mcp-permission.json"));

            TtyMcpConfigFile.SweepStale();

            // The statusline snapshots of sessions that were killed rather than closed. Not credentials, but a
            // session's spending is nobody's business once it is over.
            StatusLineRelay.SweepStale();
        }
        catch (Exception)
        {
            // Housekeeping never keeps the operator out of their cockpit. The write paths set the permissions
            // themselves, so a failure here costs the migration of an old file, not the protection of a new one.
        }
    }
}
