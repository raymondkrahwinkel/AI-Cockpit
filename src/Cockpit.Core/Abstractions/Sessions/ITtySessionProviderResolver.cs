using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Finds the TUI a profile runs, if it has one — previously unaskable: TTY mode meant <c>claude</c> regardless
/// of profile. "None" is a valid answer — a local model has no TUI — and the New-session dialog is expected to
/// accept that rather than offer a mode that cannot start.
/// </summary>
public interface ITtySessionProviderResolver
{
    /// <summary>
    /// The TTY provider for <paramref name="profile"/> (a profile-less session runs the host's default CLI), or
    /// <see langword="null"/> when that provider offers no TUI.
    /// </summary>
    ITtySessionProvider? Resolve(SessionProfile? profile);
}
