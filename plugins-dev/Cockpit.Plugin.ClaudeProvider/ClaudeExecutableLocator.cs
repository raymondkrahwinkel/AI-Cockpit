namespace Cockpit.Plugin.ClaudeProvider;

// Resolves the `claude` command to a path the plugin can spawn directly (Fase 4) — a port of the CLI-agent
// plugin's `CliExecutableLocator`, because both hit the same cross-platform trap: `System.Diagnostics.Process`
// with `UseShellExecute=false` does not consult `PATHEXT`, so a bare `"claude"` fails to launch a
// `claude.cmd` npm shim on Windows even though it is on PATH. An absolute/rooted path (a pinned executable)
// is returned unchanged; a bare name is probed against every PATH directory, then — on Windows — against the
// native installer's own location, so a blank profile just works on a machine with the desktop install.
// The native Windows install (Claude desktop's bundled claude-code) is not on PATH: it lives under
// `%APPDATA%\Claude\claude-code\&lt;version&gt;\claude.exe`, one directory per installed version. A bare
// `claude` therefore fails a pure PATH probe even though the CLI is installed, which is why a fresh profile
// showed "Not found on PATH" until the operator pasted the absolute path by hand. `Resolve` now falls
// back to that location and picks the newest version. A pinned `ExecutablePath` on the profile still bypasses
// all of this and is the reliable path on any OS.
internal static class ClaudeExecutableLocator
{
    private static readonly string[] _WindowsExecutableExtensions = [".cmd", ".exe", ".bat"];

    // Resolves `command` to a spawnable path. Rooted paths pass through unchanged; then, if a
    // `managedResolver` is given, a cockpit-managed install of the command (AC-20) wins over PATH;
    // otherwise a bare command name is looked up on PATH (Windows: trying `.cmd`/`.exe`/`.bat` per
    // directory) and then, on Windows, against the native installer's `%APPDATA%\Claude\claude-code` location.
    // If nothing is found, the command is returned unchanged so `System.Diagnostics.Process.Start()`
    // still gets a real attempt (and a diagnosable "file not found" if it truly is not installed).
    //
    // `command`: The configured command — an absolute pin, or a bare name like `claude`.
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
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_TryDirectory(directory, command) is { } resolved)
            {
                return resolved;
            }
        }

        var isDefaultCommand = command.Equals("claude", StringComparison.Ordinal)
            || (OperatingSystem.IsWindows() && command.Equals("claude.exe", StringComparison.OrdinalIgnoreCase));

        // Native installs use ~/.local/bin on every platform; GUI launches commonly omit it from PATH.
        if (isDefaultCommand && _TryUserProfileInstall(command) is { } fromUserProfile)
        {
            return fromUserProfile;
        }

        if (OperatingSystem.IsWindows() && isDefaultCommand
            && _TryWindowsDesktopInstall() is { } fromDesktopInstall)
        {
            return fromDesktopInstall;
        }

        return command;
    }

    // The native launcher's ~/.local/bin location is cross-platform; the remaining fallbacks are Unix-only.
    private static string? _TryUserProfileInstall(string command)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        if (_TryDirectory(Path.Combine(home, ".local", "bin"), command) is { } launcher)
        {
            return launcher;
        }

        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var legacyLauncher = Path.Combine(home, ".claude", "local", "claude");
        if (File.Exists(legacyLauncher))
        {
            return legacyLauncher;
        }

        return PickNewestClaudeBinary(Path.Combine(home, ".local", "share", "claude", "versions"));
    }

    // Given the installer's versions directory (files named by version, e.g. `2.1.211`, each the binary
    // itself), returns the highest-versioned one — the fallback for when the launcher symlink is missing but the
    // versioned binaries are present. Internal for testing.
    internal static string? PickNewestClaudeBinary(string versionsDirectory)
    {
        if (!Directory.Exists(versionsDirectory))
        {
            return null;
        }

        string? newest = null;
        Version? newestVersion = null;
        foreach (var file in Directory.EnumerateFiles(versionsDirectory))
        {
            if (Version.TryParse(Path.GetFileName(file), out var version)
                && (newestVersion is null || version > newestVersion))
            {
                newestVersion = version;
                newest = file;
            }
        }

        return newest;
    }

    // The native Windows install location: `%APPDATA%\Claude\claude-code`, holding one directory per installed
    // version. Returns the newest version's `claude.exe`, or `null` if the install is absent.
    private static string? _TryWindowsDesktopInstall()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(appData))
        {
            return null;
        }

        return PickNewestClaudeExe(Path.Combine(appData, "Claude", "claude-code"));
    }

    // Given the install root (`...\Claude\claude-code`), returns the `claude.exe` of the highest installed
    // version — versions are the per-version subdirectory names (e.g. `2.1.209`), compared as
    // `Version` so `2.1.209` beats `2.1.99`. Directories whose name is not a version, or that
    // hold no `claude.exe`, are only used if no properly-versioned install exists. Internal for testing.
    internal static string? PickNewestClaudeExe(string installRoot)
    {
        if (!Directory.Exists(installRoot))
        {
            return null;
        }

        string? newest = null;
        Version? newestVersion = null;
        string? unversionedFallback = null;

        foreach (var directory in Directory.EnumerateDirectories(installRoot))
        {
            var executable = Path.Combine(directory, "claude.exe");
            if (!File.Exists(executable))
            {
                continue;
            }

            if (Version.TryParse(Path.GetFileName(directory), out var version))
            {
                if (newestVersion is null || version > newestVersion)
                {
                    newestVersion = version;
                    newest = executable;
                }
            }
            else
            {
                unversionedFallback ??= executable;
            }
        }

        return newest ?? unversionedFallback;
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

        if (OperatingSystem.IsWindows() && !Path.HasExtension(command))
        {
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

        return File.Exists(candidate) ? candidate : null;
    }
}
