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
/// PowerShell 7 (<c>pwsh</c>) if installed, else Windows PowerShell, then <c>cmd</c>, then <c>wsl</c> when a distro is
/// present. On Linux/macOS the login shell (<c>$SHELL</c>) leads, then <c>bash</c>/<c>zsh</c>/<c>sh</c>. The pure
/// <see cref="Build"/> overload takes the environment and a file-probe so the per-OS logic is unit-testable off the
/// host it describes.
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
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            Environment.GetEnvironmentVariable("SHELL"),
            Environment.GetEnvironmentVariable("COMSPEC"),
            File.Exists);

    /// <summary>
    /// The per-OS detection, pure over its inputs so it can be tested without the shells it looks for. Each candidate
    /// is resolved against <paramref name="pathVariable"/> (and, on Windows, <c>PATHEXT</c>-style extension probing)
    /// via <paramref name="fileExists"/>; unresolved candidates are dropped rather than offered as a path that fails
    /// to spawn. Internal for unit tests.
    /// </summary>
    internal static IReadOnlyList<ShellDescriptor> Build(
        bool isWindows,
        string pathVariable,
        string? shellEnvironmentVariable,
        string? comSpec,
        Func<string, bool> fileExists)
    {
        var candidates = isWindows
            ? _WindowsCandidates(comSpec)
            : _UnixCandidates(shellEnvironmentVariable);

        var shells = new List<ShellDescriptor>();
        var seenPaths = new HashSet<string>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var (id, displayName, command, arguments) in candidates)
        {
            if (_Resolve(command, pathVariable, isWindows, fileExists) is not { } executable)
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
    /// Resolves a shell command to an absolute path, or null when it is not on this machine. A rooted path is taken as
    /// given (subject to the file probe); a bare name is looked up on PATH — on Windows trying <c>.exe</c>/<c>.cmd</c>/
    /// <c>.bat</c> per directory, the same reason the Claude locator does (<c>Process</c> does no <c>PATHEXT</c> lookup).
    /// </summary>
    private static string? _Resolve(string command, string pathVariable, bool isWindows, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command))
        {
            return fileExists(command) ? command : null;
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

            if (isWindows && !Path.HasExtension(command))
            {
                foreach (var extension in _WindowsExecutableExtensions)
                {
                    if (fileExists(candidate + extension))
                    {
                        return candidate + extension;
                    }
                }

                continue;
            }

            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
