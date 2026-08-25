namespace Cockpit.Plugin.CliAgentProvider;

// Resolves the configured `CliAgentConfig.Command` to a spawnable executable path (#45 fase B1).
// B2 caveat: Codex/Gemini typically come from `npm i -g` — a `.cmd` shim on Windows that `Process`
// with `UseShellExecute=false` won't find via PATHEXT, so this probes `.cmd`/`.exe`/`.bat` siblings.
internal static class CliExecutableLocator
{
    private static readonly string[] _WindowsExecutableExtensions = [".cmd", ".exe", ".bat"];

    // Resolves `command` to a path `ProcessCliSubprocess` can spawn directly: a rooted path wins outright,
    // then a cockpit-managed install (AC-20) beats PATH, then PATH itself (trying `.cmd`/`.exe`/`.bat` per
    // directory on Windows). If nothing resolves, `command` is returned unchanged for a real, diagnosable attempt.
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

        // Linux/macOS: an npm/bun/pipx CLI in ~/.local/bin (or ~/.bun/bin) is on a login shell's PATH but
        // not on a GUI or AppImage launch's, so fall back there before giving up — only for a bare command
        // name; a relative path the operator typed is theirs to own.
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
