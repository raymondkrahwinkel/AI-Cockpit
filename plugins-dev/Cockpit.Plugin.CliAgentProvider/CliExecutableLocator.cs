namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// Resolves the configured <see cref="CliAgentConfig.Command"/> to a spawnable executable path (#45 fase B1).
/// </summary>
/// <remarks>
/// B2 caveat (design doc §4 "cross-platform exe-detectie"): unlike Claude's bundled <c>claude.exe</c>
/// (<c>ClaudeExecutableLocator</c> finds it under a known <c>%APPDATA%</c> path), Codex/Gemini typically come
/// from <c>npm i -g</c> — a <c>.cmd</c> shim on Windows, a plain script on *nix. <see cref="Process"/> with
/// <c>UseShellExecute=false</c> does not consult <c>PATHEXT</c> the way a shell does, so a bare <c>"codex"</c>
/// will fail to launch a <c>codex.cmd</c> shim even though it is on PATH. This best-effort resolver probes for
/// <c>.cmd</c>/<c>.exe</c>/<c>.bat</c> siblings on Windows; it has not been verified against Raymond's real
/// npm-global install location — B2 is to confirm/adjust the discovered path there, not restructure this class.
/// </remarks>
internal static class CliExecutableLocator
{
    private static readonly string[] _WindowsExecutableExtensions = [".cmd", ".exe", ".bat"];

    /// <summary>
    /// Resolves <paramref name="command"/> to a path <see cref="ProcessCliSubprocess"/> can spawn directly.
    /// An absolute/rooted path (including one that already has an extension) is returned unchanged. A bare
    /// command name is probed against every PATH directory (Windows: trying <c>.cmd</c>/<c>.exe</c>/<c>.bat</c>
    /// per directory, since <see cref="System.Diagnostics.Process"/> does not do PATHEXT resolution itself);
    /// if nothing is found, <paramref name="command"/> is returned unchanged so <see cref="System.Diagnostics.Process.Start()"/>
    /// still gets a real attempt (and a real, diagnosable "file not found" if it truly is not installed).
    /// </summary>
    public static string Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || Path.IsPathRooted(command))
        {
            return command;
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
        // but not on a GUI or AppImage launch's — so a bare "codex" fails to resolve and the session cannot spawn
        // even though it is installed. Fall back to the standard user-local bins before giving up. Only for a bare
        // command name (no separator); a relative path the operator typed is theirs to own.
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
