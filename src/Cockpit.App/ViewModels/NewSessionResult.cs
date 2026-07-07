using Cockpit.Core.Profiles;

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
public sealed record NewSessionResult(
    SessionKind Kind,
    ClaudeProfile Profile,
    PermissionModeOption Mode,
    ModelOption Model,
    EffortOption Effort,
    string? SessionName);
