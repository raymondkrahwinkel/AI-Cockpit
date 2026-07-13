using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The choices confirmed in the New-session dialog, handed to the cockpit to mint and immediately
/// start a session (#31/#32). <see cref="Kind"/> is picked inside the dialog itself and tells the
/// cockpit which session type to create. Both kinds carry all four remaining fields: for TTY these are
/// launch-only start defaults passed as CLI flags (<c>--permission-mode</c>/
/// <c>--dangerously-skip-permissions</c>, <c>--model</c>, <c>--effort</c>) — once running, the real TUI
/// owns any live switching itself (<c>/model</c>, <c>/effort</c>, Shift+Tab), since TTY mode has no
/// control channel.
/// </summary>
/// <param name="EnabledMcpServerNames">
/// The per-session MCP-server selection (#44) picked in the dialog's checklist of the shared registry's
/// enabled servers — <see langword="null"/> when the dialog found no registry servers to offer, meaning
/// no session-level restriction applies on top of the registry's own enabled/scope filtering. Consumed by
/// the Claude SDK/local-model tool-loop (<c>McpToolProvider</c>) and the Claude-CLI <c>--mcp-config</c>
/// fan-out (<c>ClaudeCliProcess</c>); the TTY driver does not fan the registry out at all today, so this
/// has no effect there.
/// </param>
/// <param name="WorkingDirectory">
/// An optional per-session working directory chosen in the dialog (e.g. a project folder), overriding the
/// global <c>Claude:WorkingDirectory</c> option for this one session — the directory <c>claude</c> is
/// launched in, for both the SDK process and the TTY pty. <see langword="null"/>/blank keeps the global
/// default (the configured option, else the app's current directory).
/// </param>
public sealed record NewSessionResult(
    SessionKind Kind,
    SessionProfile Profile,
    PermissionModeOption Mode,
    ModelOption Model,
    EffortOption Effort,
    string? SessionName,
    IReadOnlySet<string>? EnabledMcpServerNames = null,
    string? WorkingDirectory = null,
    SessionResume? Resume = null);
