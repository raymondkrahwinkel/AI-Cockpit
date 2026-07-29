using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Which session kinds a profile can actually open, in one place. The New-session dialog asks it to decide whether
/// to offer the choice at all, and the project quick start (AC-164) asks it because it makes that choice without a
/// dialog — the same question from two doors, and two copies of the answer would eventually disagree about what a
/// profile starts as.
/// </summary>
public static class SessionKindDefaults
{
    /// <summary>
    /// Whether <paramref name="profile"/> has a TUI to run: Claude's own, or one a plugin registered for its
    /// provider. False for a profile whose provider offers only an SDK route (a local HTTP model, say), which is
    /// what makes an SDK session the only honest thing to start for it.
    /// </summary>
    public static bool HasTtyRoute(SessionProfile? profile, ITtySessionProviderResolver? ttyProviders) =>
        profile?.Claude is not null || ttyProviders?.Resolve(profile) is not null;

    /// <summary>
    /// The kind (AC-139) the New-session dialog should pre-select for <paramref name="profile"/>: its saved
    /// <see cref="SessionProfile.DefaultKind"/>, falling back to TTY (the long-standing hard default, and what a
    /// profile saved before this setting existed still gets) — unless <paramref name="profile"/> has no TTY route
    /// at all (<see cref="HasTtyRoute"/>), in which case SDK wins regardless of the saved default: offering a kind
    /// the profile cannot actually launch would be a silent no-op once Start is pressed.
    /// </summary>
    public static SessionKind ResolveDefaultKind(SessionProfile? profile, ITtySessionProviderResolver? ttyProviders)
    {
        if (!HasTtyRoute(profile, ttyProviders))
        {
            return SessionKind.Sdk;
        }

        return profile?.DefaultKind == ProfileSessionKind.Sdk ? SessionKind.Sdk : SessionKind.Tty;
    }
}
