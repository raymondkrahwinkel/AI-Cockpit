using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions.Tty;

// Housekeeping for the `--mcp-config` files earlier cockpit versions wrote for a TTY session.
//
// This host-side writer (`Write`/`Delete`) is gone (AC-380: it had no production caller once the
// provider plugins started building their own spawn config — e.g. `ClaudeMcpConfig` — from the servers the
// plugin adapter resolves, each writing and owning its own session-scoped file). What remains is the sweep: an
// operator who upgrades from a version that still wrote here, or whose machine still carries the pre-owner-only
// generation's leftovers, must not be left with a stale file holding a live token.
//
// Those files carried whatever the MCP registry carried — which includes `Authorization: Bearer` headers,
// i.e. real credentials. The oldest generation wrote to `Path.GetTempPath` at the umask's
// permissions and never deleted, so a live token could sit world-readable in a 1777 directory for as long as the
// machine stood; the next generation moved the write beside the rest of the cockpit's state, owner-only, deleted
// on session end. This sweep still claims both generations' leftovers on every start.
internal static class TtyMcpConfigFile
{
    private const string FilePrefix = "tty-mcp-";

    // The name the previous implementation used, in the temp directory. Swept, never written.
    private const string LegacyTempPattern = "cockpit-tty-mcp-*.json";

    // Where these files used to live: beside the rest of the cockpit's state, not in the shared temp directory.
    internal static string DefaultDirectory => CockpitConfigPath.Root;

    // Removes the config files that earlier runs left behind: an older cockpit's own file from a crash or a kill
    // (the delete on session end never ran), and the ones the oldest implementation wrote into the temp
    // directory, which are the ones actually holding a live token on an operator's machine right now.
    public static void SweepStale() => SweepStale(CockpitConfigPath.Root, Path.GetTempPath());

    // Overload taking both directories, so a test sweeps its own scratch space.
    internal static void SweepStale(string configDirectory, string temporaryDirectory)
    {
        Sweep(configDirectory, $"{FilePrefix}*.json");
        Sweep(temporaryDirectory, LegacyTempPattern);
    }

    private static void Sweep(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(directory, pattern))
            {
                _Delete(path);
            }
        }
        catch (Exception)
        {
            // Housekeeping. Never a reason to fail a launch.
        }
    }

    // Best-effort: a locked or already-removed leftover is not worth failing startup over.
    private static void _Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Tried again on the next start.
        }
    }
}
