namespace Cockpit.Core.Shell;

// The master switch for the shell MCP (AC-1066): while off (the default) `cockpit-shell` is not advertised to any
// session. Mirrors TerminalAccessSettings (AC-34) — once on, the permission ceiling decides each call, never a
// per-command allow/deny list.
public sealed record ShellAccessSettings
{
    public bool Enabled { get; init; }

    public static ShellAccessSettings Default { get; } = new();
}
