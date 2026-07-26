namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// Resolves the configured <see cref="KimiConfig.Command"/> to a spawnable executable path (AC-268) — a copy
/// of <c>Cockpit.Plugin.CliAgentProvider.CliExecutableLocator</c>'s resolution order (pin &gt; managed &gt; PATH).
/// </summary>
/// <remarks>
/// <see cref="Process"/> with <c>UseShellExecute=false</c> does not consult <c>PATHEXT</c> the way a shell
/// does, so a bare <c>"kimi"</c> would fail to launch a <c>kimi.cmd</c> npm shim on Windows even though it is
/// on PATH. This best-effort resolver probes for <c>.cmd</c>/<c>.exe</c>/<c>.bat</c> siblings on Windows; it
/// has not been verified against a real npm-global <c>kimi</c> install location.
/// </remarks>
internal static class KimiExecutableLocator
{
    private static readonly string[] _WindowsExecutableExtensions = [".cmd", ".exe", ".bat"];

    /// <summary>
    /// Resolves <paramref name="command"/> to a path <see cref="ProcessCliSubprocess"/> can spawn directly.
    /// An absolute/rooted path is returned unchanged. Then, if a <paramref name="managedResolver"/> is given, a
    /// cockpit-managed install of the command (distribution lands in sub [h]) wins over PATH. Otherwise a bare
    /// command name is probed against every PATH directory; if nothing is found, <paramref name="command"/> is
    /// returned unchanged so <see cref="System.Diagnostics.Process.Start()"/> still gets a real attempt (and a
    /// real, diagnosable "file not found" if it truly is not installed).
    /// </summary>
    /// <param name="command">The configured command — an absolute pin, or a bare name like <c>kimi</c>.</param>
    /// <param name="managedResolver">
    /// Optional lookup for a cockpit-managed copy of the command (typically <c>name =&gt; host.ResolveManagedCliPath(name)</c>).
    /// Consulted only for a bare name, after a rooted pin and before PATH — so a pin always wins and a null result
    /// (nothing installed, offline, or the operator removed it) simply falls through to PATH.
    /// </param>
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
