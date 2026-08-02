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

    /// <summary>What the wizard's chooser offers, in the order it offers it — the single place a work kind is
    /// added, renamed or dropped. Still the placeholder set above; see <see cref="PlaceholderNotice"/>.</summary>
    public static IReadOnlyList<PluginWorkKindOption> All { get; } =
    [
        new(Developer, "Development", "Writing, reviewing and shipping code."),
        new(Administration, "Administration", "Records, invoicing and the paperwork around the work."),
    ];

    /// <summary>Shown wherever <see cref="All"/> is offered, so a placeholder taxonomy cannot ship looking settled.</summary>
    public const string PlaceholderNotice = "Placeholder set — the real work kinds are still to be decided.";
}
