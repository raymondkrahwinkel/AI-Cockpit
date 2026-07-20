using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Everything the TTY view needs to spawn the pty for a session, raised by
/// <see cref="TtyViewModel.LaunchRequested"/> once both the panel is configured and the view is
/// subscribed. A record rather than a hand of positional parameters: the launch already carries several pieces
/// of context, and the view is the wrong place to be counting arguments.
/// </summary>
/// <param name="Launcher">Spawns the pty. Provider-neutral.</param>
/// <param name="Provider">Which CLI runs inside it.</param>
/// <param name="Profile">Profile to run under, or null for the CLI's default identity.</param>
/// <param name="Options">
/// Launch-only start defaults in the provider's own vocabulary (Claude: permission-mode/model/effort). The TUI
/// owns any switching afterwards — TTY mode has no control channel.
/// </param>
/// <param name="WorkingDirectory">Per-session working directory, or null for the global default.</param>
/// <param name="Resume">Which conversation to pick up, or null/new for a fresh one.</param>
/// <param name="EnabledMcpServerNames">
/// The per-session MCP-server selection (#44) from the New-session dialog — the enabled server names the provider
/// narrows the shared registry to, or null for no narrowing. Without this a TTY session loaded every eligible
/// server regardless of the operator's checklist.
/// </param>
public sealed record TtyLaunchRequest(
    ITtyLauncher Launcher,
    ITtySessionProvider Provider,
    SessionProfile? Profile,
    IReadOnlyDictionary<string, string> Options,
    string? WorkingDirectory,
    SessionResume? Resume,
    IReadOnlySet<string>? EnabledMcpServerNames = null);
