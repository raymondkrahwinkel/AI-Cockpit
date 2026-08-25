namespace Cockpit.Core.Terminal;

// Finds the shells a plain terminal pane can open on this machine (#AC-25) — a shell is only offered once it
// resolves to a real absolute path. Detection is ordered by preference (first entry is the default) and runs
// against the real filesystem and OS; `Build` takes the environment as a test seam but never simulates a foreign OS.
public static class ShellCatalog
{
    // The shells present on this machine, most-preferred first, each with an absolute path. Reads the real
    // environment and filesystem; empty only on a machine with no resolvable shell at all (which should not happen).
    public static IReadOnlyList<ShellDescriptor> Detect() =>
        Build(
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            Environment.GetEnvironmentVariable("SHELL"),
            Environment.GetEnvironmentVariable("COMSPEC"));

    // Detection over an explicit environment so a test can drive it with a PATH pointing at a temp directory of
    // real shell files. Unresolved candidates are dropped, duplicates collapsed. Internal for unit tests.
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

    // A descriptor for an operator-specified custom shell (#AC-25), including third-party shells not in `Detect`.
    // Resolved to an absolute path when possible; otherwise passed through so the pty surfaces a real "not found"
    // rather than silently swapping in another shell. Returns null only for a blank command.
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

    // The login shell leads so the terminal matches the operator's own; then the common shells by name. `-l` is
    // deliberately omitted — the pty already inherits the cockpit's environment, and a login shell re-running
    // profile scripts is slower and can clobber that.
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

    // Resolves a shell command to an absolute path on this machine, or null when it is not here. A rooted path is
    // taken as given (subject to the file probe); a bare name is looked up via `HostExecutableProbe`,
    // the shared PATH/`PATHEXT` probe. Host-native by design: it only ever runs for the OS it is on.
    private static string? _Resolve(string command, string pathVariable) => HostExecutableProbe.Resolve(command, pathVariable);
}
