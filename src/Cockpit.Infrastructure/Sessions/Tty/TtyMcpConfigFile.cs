using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// The <c>--mcp-config</c> file handed to a TTY session, and its lifetime.
/// <para>
/// This file carries whatever the MCP registry carries — which includes <c>Authorization: Bearer</c> headers,
/// i.e. real credentials. It used to be written to <see cref="Path.GetTempPath"/> at the umask's permissions
/// and never deleted, so a live token sat world-readable in a 1777 directory for as long as the machine stood.
/// Now it lives beside the rest of the cockpit's state, owner-only, and is deleted when the session that needs
/// it ends.
/// </para>
/// <para>
/// Not passed inline instead: the CLI does accept <c>--mcp-config</c> as a JSON string, but a process argument
/// is visible in <c>/proc/&lt;pid&gt;/cmdline</c> to every local account. That trades a private file for a
/// token in <c>ps</c>, which is worse.
/// </para>
/// </summary>
internal static class TtyMcpConfigFile
{
    private const string FilePrefix = "tty-mcp-";

    /// <summary>The name the previous implementation used, in the temp directory. Swept, never written.</summary>
    private const string LegacyTempPattern = "cockpit-tty-mcp-*.json";

    /// <summary>Where these files live: beside the rest of the cockpit's state, not in the shared temp directory.</summary>
    internal static string DefaultDirectory => CockpitConfigPath.Root;

    /// <summary>Writes <paramref name="json"/> to a fresh owner-only file and returns its path.</summary>
    public static string Write(string json) => Write(json, DefaultDirectory);

    /// <summary>Overload taking the directory, so a test never writes into the operator's real config directory.</summary>
    internal static string Write(string json, string directory)
    {
        var path = Path.Combine(directory, $"{FilePrefix}{Guid.NewGuid():N}.json");
        CockpitConfigPath.WriteAllTextPrivate(path, json);

        return path;
    }

    /// <summary>Deletes the file. Best-effort: a session that has already exited must not fail on its own cleanup.</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Swept on the next start (see SweepStale) — a locked or already-removed file is not worth an error.
        }
    }

    /// <summary>
    /// Removes the config files that earlier runs left behind: our own from a crash or a kill (the delete on
    /// session end never ran), and the ones the previous implementation wrote into the temp directory, which
    /// are the ones actually holding a live token on an operator's machine right now.
    /// </summary>
    public static void SweepStale() => SweepStale(CockpitConfigPath.Root, Path.GetTempPath());

    /// <summary>Overload taking both directories, so a test sweeps its own scratch space.</summary>
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
                Delete(path);
            }
        }
        catch (Exception)
        {
            // Housekeeping. Never a reason to fail a launch.
        }
    }
}
