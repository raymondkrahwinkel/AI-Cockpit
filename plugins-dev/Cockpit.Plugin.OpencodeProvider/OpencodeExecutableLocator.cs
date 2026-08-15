namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: resolves `OpencodeConfig.Command` to a spawnable path — a copy of KimiExecutableLocator's
// resolution order (pin > managed > PATH), unchanged. Measured live: the official installer drops a real
// opencode.exe (not a shim) into ~/.opencode/bin; the .cmd/.bat npm/bun probing here was not exercised live.
internal static class OpencodeExecutableLocator
{
    private static readonly string[] _WindowsExecutableExtensions = [".cmd", ".exe", ".bat"];

    // Resolves `command` to a spawnable path: a rooted pin wins outright, then a managed install, then PATH.
    // An unresolved bare command is returned unchanged so Process.Start still gets a real, diagnosable
    // attempt — the readable-error path AC-783 criterion 4 asks for.
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

        // Linux/macOS: a login-shell-only install dir (~/.local/bin, ~/.bun/bin, ~/.opencode/bin) is invisible
        // to a GUI/AppImage launch — fall back to it. Only for a bare command name, not a relative path.
        if (!OperatingSystem.IsWindows()
            && command.IndexOf(Path.DirectorySeparatorChar) < 0
            && _TryUserBin(command) is { } fromUserBin)
        {
            return fromUserBin;
        }

        return command;
    }

    private static string? _TryUserBin(string command)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        // The official install script's own default (~/.opencode/bin), plus the same npm/bun user bins Kimi's
        // locator falls back to.
        string[] userBins =
        [
            Path.Combine(home, ".opencode", "bin"),
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
