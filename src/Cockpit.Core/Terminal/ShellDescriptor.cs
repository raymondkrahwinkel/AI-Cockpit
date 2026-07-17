namespace Cockpit.Core.Terminal;

/// <summary>
/// One shell the operator can open as a plain terminal pane (#AC-25) — a name to show, the program to spawn, and
/// the arguments that start it interactively. Distinct from an agent CLI (<see cref="Cockpit.Core.Abstractions.Sessions.ITtySessionProvider"/>):
/// a shell has no options, no permissions and no MCP; it is just a program in the existing pty.
/// </summary>
/// <param name="Id">Stable id (<c>pwsh</c>, <c>bash</c>, <c>cmd</c>, <c>wsl</c>) — persisted on a terminal pane, not shown.</param>
/// <param name="DisplayName">What the shell picker shows ("PowerShell", "bash", "Command Prompt").</param>
/// <param name="ExecutablePath">
/// An absolute, spawnable path. Resolved at detection time: a bare <c>pwsh</c> is not directly spawnable on Windows
/// (<see cref="System.Diagnostics.Process"/> with <c>UseShellExecute=false</c> does no <c>PATHEXT</c> lookup), so the
/// catalogue only ever surfaces shells it could resolve to a real file.
/// </param>
/// <param name="Arguments">
/// Launch arguments that keep the shell interactive — e.g. <c>-NoLogo</c> for PowerShell. Empty for shells that start
/// interactive by default (bash, cmd).
/// </param>
public sealed record ShellDescriptor(
    string Id,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments);
