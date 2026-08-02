using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// Everything the TTY view needs to spawn the pty for a session, raised by
// `TtyViewModel.LaunchRequested` once both the panel is configured and the view is
// subscribed. A record rather than a hand of positional parameters: the launch already carries several pieces
// of context, and the view is the wrong place to be counting arguments.
//
// `Launcher`: Spawns the pty. Provider-neutral.
// `Provider`: Which CLI runs inside it.
// `Profile`: Profile to run under, or null for the CLI's default identity.
// `Options`:
// Launch-only start defaults in the provider's own vocabulary (Claude: permission-mode/model/effort). The TUI
// owns any switching afterwards — TTY mode has no control channel.
// `WorkingDirectory`: Per-session working directory, or null for the global default.
// `Resume`: Which conversation to pick up, or null/new for a fresh one.
// `EnabledMcpServerNames`:
// The per-session MCP-server selection (#44) from the New-session dialog — the enabled server names the provider
// narrows the shared registry to, or null for no narrowing. Without this a TTY session loaded every eligible
// server regardless of the operator's checklist.
// `Contributed`: What the plugins give this session (AC-165), or null for nothing contributed.
// `ProjectId`:
// The project this session was started under (AC-218), or null for one belonging to none — carried onto the
// `TtyLaunchContext` so a provider that fans the registry into its config resolves it against that
// project's own registry view instead of the unscoped registry.
public sealed record TtyLaunchRequest(
    ITtyLauncher Launcher,
    ITtySessionProvider Provider,
    SessionProfile? Profile,
    IReadOnlyDictionary<string, string> Options,
    string? WorkingDirectory,
    SessionResume? Resume,
    IReadOnlySet<string>? EnabledMcpServerNames = null,
    SessionResources? Contributed = null,
    string? ProjectId = null);
