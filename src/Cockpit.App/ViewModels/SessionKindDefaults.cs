using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// The New-session dialog asks it to decide whether to offer the choice at all, and the project quick start (AC-164)
// asks it because it makes that choice without a dialog. The rule itself is Core's `TtyRoute` (AC-1378).
public static class SessionKindDefaults
{
    public static bool HasTtyRoute(SessionProfile? profile, ITtySessionProviderResolver? ttyProviders) =>
        TtyRoute.Exists(profile, ttyProviders);

    // The kind (AC-139) the New-session dialog should pre-select for `profile`.
    public static SessionKind ResolveDefaultKind(SessionProfile? profile, ITtySessionProviderResolver? ttyProviders) =>
        TtyRoute.IsDefault(profile, ttyProviders) ? SessionKind.Tty : SessionKind.Sdk;
}
