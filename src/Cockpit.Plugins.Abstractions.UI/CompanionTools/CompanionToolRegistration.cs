using Avalonia.Controls;
using Material.Icons;

namespace Cockpit.Plugins.Abstractions.CompanionTools;

/// <summary>
/// A companion-window mini-tool a plugin contributes (<see cref="ICockpitHost.AddCompanionTool"/>) — a small
/// panel, or a compact icon action with a status, shown in the cockpit's pop-out companion window.
/// </summary>
/// <remarks>
/// The core stays unaware of what any companion tool shows or does, the same way it stays unaware of a widget's
/// content — this record mirrors <see cref="Cockpit.Plugins.Abstractions.Widgets.WidgetRegistration"/>.
/// </remarks>
/// <param name="Id">
/// A stable, unique id for the tool, namespaced by the plugin (e.g. "system-monitor.usage"). Treat it as an API
/// surface: changing it orphans anything that referenced the old id.
/// </param>
/// <param name="Title">
/// The tool's display name, shown as its panel header.
/// </param>
/// <param name="CreateView">
/// Builds the tool's control, on the UI thread, handed this tool's own <see cref="ICompanionToolContext"/>
/// (per-tool storage, the session-observe surface, a refresh signal).
/// </param>
public sealed record CompanionToolRegistration(string Id, string Title, Func<ICompanionToolContext, Control> CreateView)
{
    /// <summary>
    /// Hover text for the tool's compact icon action. Empty by default.
    /// </summary>
    public string Tooltip { get; init; } = string.Empty;

    /// <summary>
    /// A short glyph for the tool's icon action, shown when <see cref="IconKind"/> is null. Empty by default.
    /// </summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>
    /// A bundled vector icon for the tool's icon action, preferred over <see cref="Icon"/> when set. Null keeps
    /// the <see cref="Icon"/> string.
    /// </summary>
    public MaterialIconKind? IconKind { get; init; }
}
