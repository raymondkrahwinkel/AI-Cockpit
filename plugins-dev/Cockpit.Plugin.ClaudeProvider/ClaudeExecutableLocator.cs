namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Resolves the <c>claude</c> command to a path the plugin can spawn directly (Fase 4) — a port of the CLI-agent
/// plugin's <c>CliExecutableLocator</c>, because both hit the same cross-platform trap: <see cref="System.Diagnostics.Process"/>
/// with <c>UseShellExecute=false</c> does not consult <c>PATHEXT</c>, so a bare <c>"claude"</c> fails to launch a
/// <c>claude.cmd</c> npm shim on Windows even though it is on PATH. An absolute/rooted path (a pinned executable)
/// is returned unchanged; a bare name is probed against every PATH directory, then — on Windows — against the
/// native installer's own location, so a blank profile just works on a machine with the desktop install.
/// </summary>
/// <remarks>
/// The native Windows install (Claude desktop's bundled claude-code) is not on PATH: it lives under
/// <c>%APPDATA%\Claude\claude-code\&lt;version&gt;\claude.exe</c>, one directory per installed version. A bare
/// <c>claude</c> therefore fails a pure PATH probe even though the CLI is installed, which is why a fresh profile
/// showed "Not found on PATH" until the operator pasted the absolute path by hand. <see cref="Resolve"/> now falls
/// back to that location and picks the newest version. A pinned <c>ExecutablePath</c> on the profile still bypasses
/// all of this and is the reliable path on any OS.
/// </remarks>
internal static class ClaudeExecutableLocator
{
    private static readonly string[] _WindowsExecutableExtensions = [".cmd", ".exe", ".bat"];

    /// <summary>
    /// Resolves <paramref name="command"/> to a spawnable path. Rooted paths pass through unchanged; a bare command
    /// name is looked up on PATH (Windows: trying <c>.cmd</c>/<c>.exe</c>/<c>.bat</c> per directory) and then, on
    /// Windows, against the native installer's <c>%APPDATA%\Claude\claude-code</c> location. If nothing is found,
    /// the command is returned unchanged so <see cref="System.Diagnostics.Process.Start()"/> still gets a real
    /// attempt (and a diagnosable "file not found" if it truly is not installed).
    /// </summary>
    public static string Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || Path.IsPathRooted(command))
        {
            return command;
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

        return command;
    }

    /// <summary>
    /// The native Windows install location: <c>%APPDATA%\Claude\claude-code</c>, holding one directory per installed
    /// version. Returns the newest version's <c>claude.exe</c>, or <see langword="null"/> if the install is absent.
    /// </summary>
    private static string? _TryWindowsDesktopInstall()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(appData))
        {
            return null;
        }

        return PickNewestClaudeExe(Path.Combine(appData, "Claude", "claude-code"));
    }

    /// <summary>
    /// Given the install root (<c>...\Claude\claude-code</c>), returns the <c>claude.exe</c> of the highest installed
    /// version — versions are the per-version subdirectory names (e.g. <c>2.1.209</c>), compared as
    /// <see cref="Version"/> so <c>2.1.209</c> beats <c>2.1.99</c>. Directories whose name is not a version, or that
    /// hold no <c>claude.exe</c>, are only used if no properly-versioned install exists. Internal for testing.
    /// </summary>
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
