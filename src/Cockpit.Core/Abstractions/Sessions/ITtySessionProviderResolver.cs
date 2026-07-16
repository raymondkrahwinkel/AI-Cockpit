using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Finds the TUI a profile runs, if it has one.
/// <para>
/// Which is a question the cockpit could not previously ask: TTY mode meant <c>claude</c>, and a session started
/// as a TTY under any other profile would have launched Claude's CLI regardless of what the profile said. The
/// answer is allowed to be "none" — a local model has no TUI to host — and the New-session dialog is expected to
/// take that for an answer rather than offering a mode that cannot start.
/// </para>
/// </summary>
public interface ITtySessionProviderResolver
{
    /// <summary>
    /// The TTY provider for <paramref name="profile"/> (a profile-less session runs the host's default CLI), or
    /// <see langword="null"/> when that provider offers no TUI.
    /// </summary>
    ITtySessionProvider? Resolve(SessionProfile? profile);
}
