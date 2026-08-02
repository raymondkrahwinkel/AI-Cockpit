namespace Cockpit.Core.Plugins;

// The pure abstractions-compatibility check behind the drift warning. A compiled plugin carries, in its
// assembly metadata, the `Cockpit.Plugins.Abstractions` version it was built against; the host knows the
// one it actually ships. A plugin built against a *newer* SDK than the host may call members this host
// does not have — it loads (the reference resolves to the host's assembly) and then fails somewhere the
// operator cannot see. That is the case worth saying out loud, and this derives it from what the plugin was
// built against rather than a `minHostVersion` a manifest can claim and never keep.
public static class AbstractionsCompatibility
{
    // True when `builtAgainst` is a newer Cockpit.Plugins.Abstractions than the running
    // `host` — the one direction that can break out of sight. A plugin built against an older
    // SDK is safe, because the contract only grows additively within a major, so everything it calls still
    // exists. A null `builtAgainst` (the version could not be read) is treated as compatible:
    // a missing stamp is not evidence of a mismatch, and warning over it would cry wolf.
    public static bool BuiltAgainstNewerHost(Version? builtAgainst, Version host) =>
        builtAgainst is not null && builtAgainst > host;
}
