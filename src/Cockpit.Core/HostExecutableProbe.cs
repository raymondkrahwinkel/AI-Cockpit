using Cockpit.Core.Plugins;

namespace Cockpit.Core;

// The host-side half of first-run provider detection (AC-510[b]): whether an executable named
// `claude`/`codex`/`gemini`/… exists on PATH. Deliberately says only "found", never "works" — login
// state and whether the CLI runs are for the owning plugin to know; this probe never spawns it.
public static class HostExecutableProbe
{
    private static readonly string[] _WindowsExecutableExtensions = [".exe", ".cmd", ".bat"];

    // Resolves `command` against this process's real PATH, or null when it is not there.
    public static string? Resolve(string command) =>
        Resolve(command, Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

    public static string? Resolve(ProviderHostExecutable executable) =>
        Resolve(
            executable,
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string? Resolve(ProviderHostExecutable executable, string pathVariable, string userProfile)
    {
        if (Resolve(executable.Command, pathVariable) is { } fromPath)
        {
            return fromPath;
        }

        if (string.IsNullOrEmpty(userProfile))
        {
            return null;
        }

        foreach (var relativeDirectory in executable.UserProfileRelativeDirectories)
        {
            var directory = Path.Combine(userProfile, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (Resolve(executable.Command, directory) is { } resolved)
            {
                return resolved;
            }
        }

        return null;
    }

    // The probe over an explicit PATH-shaped string, so a test can drive it against a temp directory of real files. Internal for unit tests.
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
