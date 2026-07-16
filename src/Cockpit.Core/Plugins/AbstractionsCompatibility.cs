namespace Cockpit.Core.Plugins;

/// <summary>
/// The pure abstractions-compatibility check behind the drift warning. A compiled plugin carries, in its
/// assembly metadata, the <c>Cockpit.Plugins.Abstractions</c> version it was built against; the host knows the
/// one it actually ships. A plugin built against a <em>newer</em> SDK than the host may call members this host
/// does not have — it loads (the reference resolves to the host's assembly) and then fails somewhere the
/// operator cannot see. That is the case worth saying out loud, and this derives it from what the plugin was
/// built against rather than a <c>minHostVersion</c> a manifest can claim and never keep.
/// </summary>
public static class AbstractionsCompatibility
{
    /// <summary>
    /// True when <paramref name="builtAgainst"/> is a newer Cockpit.Plugins.Abstractions than the running
    /// <paramref name="host"/> — the one direction that can break out of sight. A plugin built against an older
    /// SDK is safe, because the contract only grows additively within a major, so everything it calls still
    /// exists. A null <paramref name="builtAgainst"/> (the version could not be read) is treated as compatible:
    /// a missing stamp is not evidence of a mismatch, and warning over it would cry wolf.
    /// </summary>
    public static bool BuiltAgainstNewerHost(Version? builtAgainst, Version host) =>
        builtAgainst is not null && builtAgainst > host;
}
