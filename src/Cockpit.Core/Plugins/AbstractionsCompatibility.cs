namespace Cockpit.Core.Plugins;

// AC-1013: Drift check — a plugin built against a newer Abstractions than the host may call members
// the host lacks and fail silently later; only the newer-than-host direction is unsafe to skip.
// (Omitted: full derivation vs. relying on a manifest-claimed minHostVersion; see ticket for detail.)
public static class AbstractionsCompatibility
{
    // AC-1013: newer-than-host is the only unsafe direction (older SDKs only add members, so nothing
    // breaks); a null builtAgainst means unreadable, not mismatched, so it is treated as compatible.
    public static bool BuiltAgainstNewerHost(Version? builtAgainst, Version host) =>
        builtAgainst is not null && builtAgainst > host;
}
