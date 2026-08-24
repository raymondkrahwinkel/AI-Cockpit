namespace Cockpit.Core.Plugins;

// AC-1013: A plugin this build has replaced with others, and the pure decision of whether to say so —
// splitting/folding a plugin leaves the old one installed (never removed), so it keeps claiming the
// same widget/workspace types as its successors (Id/DisplayName/SuccessorIds: replaced/label/takeover).
public sealed record SupersededPlugin(string Id, string DisplayName, IReadOnlyList<string> SuccessorIds)
{
    // What this build knows it has replaced. It should stay short: this is a migration aid, not a general
    // mechanism — an entry earns its place by an operator otherwise being left with two plugins claiming the
    // same widget or workspace type.
    public static readonly IReadOnlyList<SupersededPlugin> Known =
    [
        // Split 2026-07-15 (Raymond: "als ik wel de clock wil maar niet de system monitor, wil ik dus alleen de
        // clock downloaden en installeren"). The successors kept the widget type ids ("widgets.clock"), so a
        // saved dashboard survives — which is exactly why the old plugin cannot be left beside them.
        new("widgets", "Reference widgets", ["clock", "system-monitor"]),

        // Merged 2026-08-16 (AC-836): the whiteboard moved into the diagram plugin, which kept the workspace type
        // id "whiteboard.panel" — so a saved workspace survives, and both plugins claim that type until one goes.
        new("whiteboard", "Whiteboard", ["diagram"]),
    ];

    // AC-1013: Tell the operator only when both this plugin and a successor are loaded — `loadedIds`
    // is the plugins that actually loaded (not merely registered), since only a loaded plugin claims a
    // widget type, the reason there's anything to say.
    public bool ShouldOffer(IReadOnlyCollection<string> loadedIds) =>
        loadedIds.Contains(Id) && SuccessorIds.Any(loadedIds.Contains);
}
