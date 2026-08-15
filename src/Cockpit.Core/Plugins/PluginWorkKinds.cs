namespace Cockpit.Core.Plugins;

// AC-511 criterion 6 (Raymond, 2026-08-15): the work-kind wizard's chip set. Only "developer" is concrete today —
// every other plugin stays generic (empty/absent `PluginStoreEntry.Audience`) until a kind of its own is decided.
public static class PluginWorkKinds
{
    public const string Developer = "developer";

    // What the wizard's chooser offers, in the order it offers it — the single place a work kind is
    // added, renamed or dropped.
    public static IReadOnlyList<PluginWorkKindOption> All { get; } =
    [
        new(Developer, "Development", "Writing, reviewing and shipping code."),
    ];
}
