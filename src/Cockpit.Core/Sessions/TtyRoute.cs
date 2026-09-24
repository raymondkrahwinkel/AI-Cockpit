using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Sessions;

// AC-1378: which profiles have a terminal route and start on it by default, one rule for the desktop's New-session
// dialog and quick start (`SessionKindDefaults`) and the backend launcher that has to refuse them.
public static class TtyRoute
{
    // Claude's own TUI, or one a plugin registered for the profile's provider. False for a provider that offers only an
    // SDK route (a local HTTP model, say).
    public static bool Exists(SessionProfile? profile, ITtySessionProviderResolver? ttyProviders) =>
        profile?.Claude is not null || ttyProviders?.Resolve(profile) is not null;

    // The profile's saved `SessionProfile.DefaultKind`, falling back to TTY (the long-standing hard default, and what a
    // profile saved before that setting existed still gets) whenever there is a route to fall back to.
    public static bool IsDefault(SessionProfile? profile, ITtySessionProviderResolver? ttyProviders) =>
        Exists(profile, ttyProviders) && profile?.DefaultKind != ProfileSessionKind.Sdk;
}
