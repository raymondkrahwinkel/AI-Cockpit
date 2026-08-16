namespace Cockpit.Core.Plugins;

// A plugin this build has replaced with others, and the pure decision of whether to say so. Splitting a plugin
// in two — or folding one into another — leaves the old one installed (the installer never removes what an
// operator has), so it keeps claiming the same widget or workspace types as its successors, and one loses.
//
// `Id`: The folder id of the plugin that has been replaced.
// `DisplayName`: What to call it when telling the operator.
// `SuccessorIds`: The plugins that took over. Nothing is said until at least one of them is actually enabled.
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

    // Whether to tell the operator about this one: it is still loaded, and at least one successor is loaded too
    // and has taken over from it. Neither half alone is worth a word — an old plugin with no successor is just a
    // plugin, and a successor without the old one is the ordinary case.
    //
    // `loadedIds`:
    // The folder ids of the plugins that actually loaded, not the ones with a registration. Only a loaded plugin
    // claims a widget type, which is the whole reason there is anything to say.
    public bool ShouldOffer(IReadOnlyCollection<string> loadedIds) =>
        loadedIds.Contains(Id) && SuccessorIds.Any(loadedIds.Contains);
}
