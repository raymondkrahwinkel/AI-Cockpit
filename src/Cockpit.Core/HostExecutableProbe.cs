namespace Cockpit.Core;

/// <summary>
/// The host-side half of first-run provider detection (AC-510[b]): whether an executable named
/// <c>claude</c>/<c>codex</c>/<c>gemini</c>/… exists on PATH, the one question the host can answer before any
/// provider plugin is installed. Resolves a bare name the same way <see cref="Terminal.ShellCatalog"/> resolves a
/// shell — trying <c>.exe</c>/<c>.cmd</c>/<c>.bat</c> per PATH directory on Windows, since
/// <see cref="System.Diagnostics.Process"/> does not consult <c>PATHEXT</c> for a bare command itself.
/// </summary>
/// <remarks>
/// Deliberately says only "found", never "works": a plugin-managed install, a login/auth state and whether the CLI
/// actually runs are all things only the owning plugin can know, and only after it is installed — this probe never
/// spawns the executable, it only checks that a file with that name exists.
/// </remarks>
public static class HostExecutableProbe
{
    private static readonly string[] _WindowsExecutableExtensions = [".exe", ".cmd", ".bat"];

    /// <summary>Resolves <paramref name="command"/> against this process's real PATH, or null when it is not there.</summary>
    public static string? Resolve(string command) =>
        Resolve(command, Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

    /// <summary>The probe over an explicit PATH-shaped string, so a test can drive it against a temp directory of real files. Internal for unit tests.</summary>
    internal static string? Resolve(string command, string pathVariable)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command))
        {
            return File.Exists(command) ? command : null;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, command);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry (stray quote/invalid char) — skip it, don't fail the whole probe.
                continue;
            }

            if (OperatingSystem.IsWindows() && !Path.HasExtension(command))
            {
                foreach (var extension in _WindowsExecutableExtensions)
                {
                    if (File.Exists(candidate + extension))
                    {
                        return candidate + extension;
                    }
                }

                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
