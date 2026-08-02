namespace Cockpit.Core.Plugins;

/// <summary>
/// PLACEHOLDER (AC-511) — not a decided taxonomy. Raymond's own domain knowledge picks the real set of work
/// kinds and which plugins default to which; this only gives that decision one place to land rather than a
/// repo-wide search. <see cref="PluginStoreEntry.WorkKind"/> is a free string, so nothing here is enforced —
/// an index carrying a value not listed below still round-trips unchanged.
/// </summary>
public static class PluginWorkKinds
{
    // PLACEHOLDER values only, to prove the axis is wired end to end. Replace/extend when the real set — and
    // the per-plugin defaults it implies — is decided (the work-kind screen itself, not part of AC-511 [c]).
    public const string Developer = "developer";
    public const string Administration = "administration";
}
