namespace Cockpit.Core.Sessions;

// What kind of failure a SessionError reports (AC-720). Mirrors Cockpit.Plugins.Abstractions.Sessions.
// PluginSessionErrorKind one-to-one — Core does not reference Plugins.Abstractions, so
// PluginSessionDriverAdapter maps between the two explicitly.
public enum SessionErrorKind
{
    Unknown,
    AuthRequired,
    RateLimited,
    ServiceUnavailable,
}
