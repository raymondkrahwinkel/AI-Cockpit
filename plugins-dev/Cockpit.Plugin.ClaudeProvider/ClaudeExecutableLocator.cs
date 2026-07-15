namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Resolves the <c>claude</c> command to a path the plugin can spawn directly (Fase 4) — a port of the CLI-agent
/// plugin's <c>CliExecutableLocator</c>, because both hit the same cross-platform trap: <see cref="System.Diagnostics.Process"/>
/// with <c>UseShellExecute=false</c> does not consult <c>PATHEXT</c>, so a bare <c>"claude"</c> fails to launch a
/// <c>claude.cmd</c> npm shim on Windows even though it is on PATH. An absolute/rooted path (a pinned executable)
/// is returned unchanged; a bare name is probed against every PATH directory, trying the Windows shim extensions.
/// </summary>
/// <remarks>
/// Not verified against a real Windows install here (no Windows in this sandbox) — the probe order mirrors the proven
/// Codex locator. A pinned <c>ExecutablePath</c> on the profile bypasses this entirely and is the reliable path on any
/// OS; this resolver is the best-effort fallback for a bare command name.
/// </remarks>
internal static class ClaudeExecutableLocator
{
    private static readonly string[] _WindowsExecutableExtensions = [".cmd", ".exe", ".bat"];

    /// <summary>
    /// Resolves <paramref name="command"/> to a spawnable path. Rooted paths pass through unchanged; a bare command
    /// name is looked up on PATH (Windows: trying <c>.cmd</c>/<c>.exe</c>/<c>.bat</c> per directory). If nothing is
    /// found, the command is returned unchanged so <see cref="System.Diagnostics.Process.Start()"/> still gets a real
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

        return command;
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
