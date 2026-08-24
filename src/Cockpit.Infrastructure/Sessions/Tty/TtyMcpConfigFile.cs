using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions.Tty;

// AC-1013: Housekeeping for the `--mcp-config` files earlier cockpit versions wrote for a TTY session. The
// host-side writer is gone (AC-380: provider plugins now build and own their own spawn config); what remains is
// the sweep, since these files carried real `Authorization: Bearer` credentials and must not linger stale.
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
