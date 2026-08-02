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

        // PATH did not have it — on Windows the desktop install is off-PATH, so try its well-known location before
        // giving up. Only for the bare "claude" command; a different name the operator typed is theirs to own.
        if (OperatingSystem.IsWindows()
            && (command.Equals("claude", StringComparison.OrdinalIgnoreCase) || command.Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
            && _TryWindowsDesktopInstall() is { } fromDesktopInstall)
        {
            return fromDesktopInstall;
        }

        // Linux/macOS: the `claude` installer's launcher lives at ~/.local/bin/claude, which a login shell adds to
        // PATH but a GUI or AppImage launch does not — so a blank profile reads "not found" and the session cannot
        // spawn even though claude is installed. Fall back to the well-known install locations, the same way the
        // Windows branch above does for the desktop install. Only for the bare "claude" command.
        if (!OperatingSystem.IsWindows()
            && command.Equals("claude", StringComparison.Ordinal)
            && _TryUnixWellKnownInstall() is { } fromUnixInstall)
        {
            return fromUnixInstall;
        }

        return command;
    }

    // The `claude` installer's launcher and older local-install layouts on Linux/macOS, none of which a GUI or
    // AppImage launch carries on PATH: `~/.local/bin/claude` (a symlink into the versioned install), then
    // `~/.claude/local/claude`, then the newest binary directly under `~/.local/share/claude/versions`.
    // Returns the first that exists.
    private static string? _TryUnixWellKnownInstall()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }

        string[] launchers =
        [
            Path.Combine(home, ".local", "bin", "claude"),
            Path.Combine(home, ".claude", "local", "claude"),
        ];
        foreach (var launcher in launchers)
        {
            if (File.Exists(launcher))
            {
                return launcher;
            }
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
