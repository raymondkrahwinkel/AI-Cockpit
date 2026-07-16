namespace Cockpit.Core.Terminal;

/// <summary>
/// Finds the shells a plain terminal pane can open on this machine (#AC-25). The cockpit already runs an agent CLI in
/// a pty; a terminal is the same pty pointed at a shell instead, so all this has to answer is <em>which shells are
/// actually here, and by what absolute path</em> — the same cross-platform trap as
/// <see cref="Cockpit.Core.Abstractions.Sessions.ITtySessionProvider"/>'s executables: a bare <c>pwsh</c>/<c>bash</c>
/// is not spawnable directly (no <c>PATHEXT</c>/PATH lookup by <see cref="System.Diagnostics.Process"/>), so a shell
/// is only offered once it resolves to a real file.
/// </summary>
/// <remarks>
/// Detection is best-effort and ordered by preference: the first entry is the sensible default. On Windows that is
/// PowerShell 7 (<c>pwsh</c>) if installed, else Windows PowerShell, then <c>cmd</c>, then <c>wsl</c> when present. On
/// Linux/macOS the login shell (<c>$SHELL</c>) leads, then <c>bash</c>/<c>zsh</c>/<c>sh</c>. It resolves against the
/// real filesystem and this OS — <see cref="Build"/> is a seam that takes the environment values so a test can point
/// it at a temp directory of real shell files, but it deliberately does not simulate a foreign OS: <see cref="Detect"/>
/// only ever runs on the OS it describes, so leaning on <see cref="Path"/> here is correct, not a shortcut.
/// </remarks>
public static class ShellCatalog
{
    private static readonly string[] _WindowsExecutableExtensions = [".exe", ".cmd", ".bat"];

    /// <summary>
    /// The shells present on this machine, most-preferred first, each with an absolute path. Reads the real
    /// environment and filesystem; empty only on a machine with no resolvable shell at all (which should not happen).
    /// </summary>
    public static IReadOnlyList<ShellDescriptor> Detect() =>
        Build(
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            Environment.GetEnvironmentVariable("SHELL"),
            Environment.GetEnvironmentVariable("COMSPEC"));

    /// <summary>
    /// The detection over an explicit environment (this OS, the real filesystem), so a test can drive it with a PATH
    /// pointing at a temp directory of real shell files. Each candidate is resolved via <see cref="_Resolve"/>;
    /// unresolved candidates are dropped rather than offered as a path that fails to spawn, and the same binary is
    /// never listed twice. Internal for unit tests.
    /// </summary>
    internal static IReadOnlyList<ShellDescriptor> Build(string pathVariable, string? shellEnvironmentVariable, string? comSpec)
    {
        var candidates = OperatingSystem.IsWindows()
            ? _WindowsCandidates(comSpec)
            : _UnixCandidates(shellEnvironmentVariable);

        var shells = new List<ShellDescriptor>();
        var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var (id, displayName, command, arguments) in candidates)
        {
            if (_Resolve(command, pathVariable) is not { } executable)
            {
                continue;
            }

            // Two names can resolve to the same binary (a `$SHELL` of /bin/bash plus the `bash` candidate); keep the
            // first, which is the more-preferred, so the picker never shows the same shell twice.
            if (seenPaths.Add(executable))
            {
                shells.Add(new ShellDescriptor(id, displayName, executable, arguments));
            }
        }

        return shells;
    }

    /// <summary>
    /// A descriptor for an operator-specified custom shell (#AC-25) — any path or command, including a third-party
    /// shell not in <see cref="Detect"/> (fish, nushell, xonsh, a login wrapper), which is common on Linux/macOS. The
    /// command is resolved to an absolute path when it can be (a bare name via PATH, Windows extensions probed); when
    /// it cannot, it is passed through unchanged so the pty surfaces a real "not found" the operator can fix, rather
    /// than being silently swapped for another shell. Returns null only for a blank command.
    /// </summary>
    public static ShellDescriptor? ForCommand(string command)
    {
        var trimmed = command?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        var resolved = _Resolve(trimmed, Environment.GetEnvironmentVariable("PATH") ?? string.Empty) ?? trimmed;
        var name = Path.GetFileNameWithoutExtension(trimmed);
        return new ShellDescriptor("custom", string.IsNullOrEmpty(name) ? trimmed : name, resolved, []);
    }

    // PowerShell 7 first (the modern default), then Windows PowerShell, then cmd via %COMSPEC% (always present), then
    // wsl.exe — only offered when it resolves, but note wsl with no installed distro still resolves; the launch, not
    // the catalogue, surfaces "no distro". -NoLogo keeps the PowerShell banner out of the fresh pane.
    private static IEnumerable<(string Id, string DisplayName, string Command, IReadOnlyList<string> Arguments)> _WindowsCandidates(string? comSpec) =>
    [
        ("pwsh", "PowerShell", "pwsh", (IReadOnlyList<string>)["-NoLogo"]),
        ("powershell", "Windows PowerShell", "powershell", ["-NoLogo"]),
        ("cmd", "Command Prompt", string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec, []),
        ("wsl", "WSL", "wsl.exe", []),
    ];

    // The login shell leads so the terminal matches what the operator's own terminal gives them; then the common
    // shells by name. `-l` is deliberately omitted — the pty child inherits the cockpit's environment, and a login
    // shell re-running profile scripts is slower and occasionally clobbers that; an interactive non-login shell is
    // the least-surprising default, the same choice most terminal emulators make for a new tab.
    private static IEnumerable<(string Id, string DisplayName, string Command, IReadOnlyList<string> Arguments)> _UnixCandidates(string? shellEnvironmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(shellEnvironmentVariable))
        {
            yield return ("login", _NameFromPath(shellEnvironmentVariable), shellEnvironmentVariable, []);
        }

        yield return ("bash", "bash", "bash", (IReadOnlyList<string>)[]);
        yield return ("zsh", "zsh", "zsh", []);
        yield return ("sh", "sh", "sh", []);
    }

    private static string _NameFromPath(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>
    /// Resolves a shell command to an absolute path on this machine, or null when it is not here. A rooted path is
    /// taken as given (subject to the file probe); a bare name is looked up on PATH — on Windows trying <c>.exe</c>/
    /// <c>.cmd</c>/<c>.bat</c> per directory, the same reason the Claude locator does (<c>Process</c> does no
    /// <c>PATHEXT</c> lookup). Host-native by design: it only ever runs for the OS it is on.
    /// </summary>
    private static string? _Resolve(string command, string pathVariable)
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
