using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The choices confirmed in the New-session dialog, handed to the cockpit to mint and immediately
/// start a session (#31). For a TTY session only <see cref="Profile"/> is used — the real TUI owns
/// its own mode/model/effort — so the other fields carry the profile's defaults but are ignored.
/// </summary>
public sealed record NewSessionResult(
    ClaudeProfile Profile,
    PermissionModeOption Mode,
    ModelOption Model,
    EffortOption Effort);
