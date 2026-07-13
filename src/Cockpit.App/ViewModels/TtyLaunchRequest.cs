using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Everything the TTY view needs to spawn the pty for a session, raised by
/// <see cref="ClaudeTtyViewModel.LaunchRequested"/> once both the panel is configured and the view is
/// subscribed. A record rather than a hand of positional parameters: the launch already carries six pieces of
/// context, and the view is the wrong place to be counting arguments.
/// </summary>
/// <param name="Launcher">The launcher that spawns the pty.</param>
/// <param name="Profile">Profile to run under, or null for the CLI's default identity.</param>
/// <param name="PermissionMode">Launch-only permission mode; the TUI owns any switching afterwards.</param>
/// <param name="Model">Launch-only model.</param>
/// <param name="Effort">Launch-only effort level.</param>
/// <param name="WorkingDirectory">Per-session working directory, or null for the global default.</param>
/// <param name="Resume">Which conversation to pick up, or null/new for a fresh one.</param>
public sealed record TtyLaunchRequest(
    IClaudeTtyLauncher Launcher,
    SessionProfile? Profile,
    string? PermissionMode,
    string? Model,
    string? Effort,
    string? WorkingDirectory,
    SessionResume? Resume);
