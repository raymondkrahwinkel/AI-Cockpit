namespace Cockpit.Core.Terminal;

// One shell the operator can open as a plain terminal pane (#AC-25) — a name to show, the program to spawn, and
// the arguments that start it interactively. Distinct from an agent CLI (`Cockpit.Core.Abstractions.Sessions.ITtySessionProvider`):
// a shell has no options, no permissions and no MCP; it is just a program in the existing pty.
//
// `Id`: Stable id (`pwsh`, `bash`, `cmd`, `wsl`) — persisted on a terminal pane, not shown.
// `DisplayName`: What the shell picker shows ("PowerShell", "bash", "Command Prompt").
// `ExecutablePath`:
// An absolute, spawnable path. Resolved at detection time: a bare `pwsh` is not directly spawnable on Windows
// (`System.Diagnostics.Process` with `UseShellExecute=false` does no `PATHEXT` lookup), so the
// catalogue only ever surfaces shells it could resolve to a real file.
// `Arguments`:
// Launch arguments that keep the shell interactive — e.g. `-NoLogo` for PowerShell. Empty for shells that start
// interactive by default (bash, cmd).
public sealed record ShellDescriptor(
    string Id,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments);
