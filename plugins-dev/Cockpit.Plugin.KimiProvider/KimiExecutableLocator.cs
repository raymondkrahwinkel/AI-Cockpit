namespace Cockpit.Plugin.KimiProvider;

// Resolves the configured `KimiConfig.Command` to a spawnable executable path (AC-268) — a copy
// of `Cockpit.Plugin.CliAgentProvider.CliExecutableLocator`'s resolution order (pin &gt; managed &gt; PATH).
// `Process` with `UseShellExecute=false` does not consult `PATHEXT` the way a shell
// does, so a bare `"kimi"` would fail to launch a `kimi.cmd` npm shim on Windows even though it is
// on PATH. This best-effort resolver probes for `.cmd`/`.exe`/`.bat` siblings on Windows; it
// has not been verified against a real npm-global `kimi` install location.
internal static class KimiExecutableLocator
{
    private static readonly string[] _WindowsExecutableExtensions = [".cmd", ".exe", ".bat"];

    // Resolves `command` to a path `ProcessCliSubprocess` can spawn directly.
    // An absolute/rooted path is returned unchanged. Then, if a `managedResolver` is given, a
    // cockpit-managed install of the command (distribution lands in sub [h]) wins over PATH. Otherwise a bare
    // command name is probed against every PATH directory; if nothing is found, `command` is
    // returned unchanged so `System.Diagnostics.Process.Start()` still gets a real attempt (and a
    // real, diagnosable "file not found" if it truly is not installed).
    //
    // `command`: The configured command — an absolute pin, or a bare name like `kimi`.
    // `managedResolver`:
    // Optional lookup for a cockpit-managed copy of the command (typically `name =&gt; host.ResolveManagedCliPath(name)`).
    // Consulted only for a bare name, after a rooted pin and before PATH — so a pin always wins and a null result
    // (nothing installed, offline, or the operator removed it) simply falls through to PATH.
    public static string Resolve(string command, Func<string, string?>? managedResolver = null)
    {
        if (string.IsNullOrWhiteSpace(command) || Path.IsPathRooted(command))
        {
            return command;
        }

        // A managed install sits between the pin and PATH: preferred when present, invisible when absent.
        if (managedResolver?.Invoke(command) is { Length: > 0 } managed)
        {
            return managed;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in directories)
        {
            var direct = _TryDirectory(directory, command);
            if (direct is not null)
            {
                return direct;
            }
        }

        // Linux/macOS: an npm/bun/pipx CLI installed into ~/.local/bin (or ~/.bun/bin) is on a login shell's PATH
        // but not on a GUI or AppImage launch's — so a bare "kimi" fails to resolve even though it is installed.
        // Fall back to the standard user-local bins before giving up. Only for a bare command name (no
        // separator); a relative path the operator typed is theirs to own.
        if (!OperatingSystem.IsWindows()
            && command.IndexOf(Path.DirectorySeparatorChar) < 0
            && _TryUnixUserBin(command) is { } fromUserBin)
        {
            return fromUserBin;
        }

        return command;
    }

    private static string? _TryUnixUserBin(string command)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        string[] userBins =
        [
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".bun", "bin"),
        ];
        foreach (var directory in userBins)
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? _TryDirectory(string directory, string command)
    {
        string candidate;
        try
        {
            candidate = Path.Combine(directory, command);
        }
        catch (ArgumentException)
        {
            // A malformed PATH entry (stray quote/invalid char) — skip it rather than fail resolution entirely.
            return null;
        }

        if (File.Exists(candidate))
        {
            return candidate;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var extension in _WindowsExecutableExtensions)
        {
            var withExtension = candidate + extension;
            if (File.Exists(withExtension))
            {
                return withExtension;
            }
        }

        return null;
    }
}
