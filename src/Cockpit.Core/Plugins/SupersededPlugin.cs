namespace Cockpit.Core.Plugins;

/// <summary>
/// A plugin this build has replaced with others, and the pure decision of whether to say so. It exists because
/// splitting one plugin into two leaves the old one installed: the installer deliberately never removes what an
/// operator has (<c>BundledPluginInstaller</c>: "a plugin they disabled stays disabled and is left untouched on
/// disk"), which is right — but it means the old plugin keeps contributing the same widget types as its
/// successors, and only one of each can win.
/// </summary>
/// <param name="Id">The folder id of the plugin that has been replaced.</param>
/// <param name="DisplayName">What to call it when telling the operator.</param>
/// <param name="SuccessorIds">The plugins that took over. Nothing is said until at least one of them is actually installed.</param>
public sealed record SupersededPlugin(string Id, string DisplayName, IReadOnlyList<string> SuccessorIds)
{
    /// <summary>
    /// What this build knows it has replaced. One entry, and it should stay short: this is a migration aid, not
    /// a general mechanism — an entry earns its place by an operator otherwise being left with two plugins
    /// claiming the same widget.
    /// </summary>
    public static readonly IReadOnlyList<SupersededPlugin> Known =
    [
        // Split 2026-07-15 (Raymond: "als ik wel de clock wil maar niet de system monitor, wil ik dus alleen de
        // clock downloaden en installeren"). The successors kept the widget type ids ("widgets.clock"), so a
        // saved dashboard survives — which is exactly why the old plugin cannot be left beside them.
        new("widgets", "Reference widgets", ["clock", "system-monitor"]),
    ];

    /// <summary>
    /// Whether to tell the operator about this one: it is still installed, and at least one successor is there
    /// to have taken over from it. Neither half alone is worth a word — an old plugin with no successor is just
    /// a plugin, and a successor without the old one is the ordinary case.
    /// </summary>
    public bool ShouldOffer(IReadOnlyCollection<string> installedIds) =>
        installedIds.Contains(Id) && SuccessorIds.Any(installedIds.Contains);
}
