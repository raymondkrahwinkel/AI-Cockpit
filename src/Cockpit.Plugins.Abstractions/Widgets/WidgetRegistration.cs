using Avalonia.Controls;

namespace Cockpit.Plugins.Abstractions.Widgets;

/// <summary>
/// A dashboard widget type a plugin contributes (<see cref="ICockpitHost.AddWidget"/>) — the widget
/// equivalent of a session provider or a workflow step. A Dashboard workspace (the widget-hosting workspace
/// kind, see docs/workspaces-widgets-terminals.md) shows every registered type in its "Add widget" gallery;
/// picking one creates an instance, and <see cref="CreateView"/> builds that instance's control. The core
/// stays unaware of what any widget shows — a clock, CPU/RAM bars, the git state of the active session — the
/// same way it stays unaware of a provider's transcript format.
/// </summary>
/// <param name="Id">
/// A stable, unique id for the widget <em>type</em>, namespaced by the plugin (e.g. "system-monitor.usage").
/// Persisted with each placed instance so a saved dashboard rebuilds after a restart; changing it orphans
/// existing instances, so treat it as an API surface.
/// </param>
/// <param name="Title">The widget's display name, shown in the gallery and as the pane's default header.</param>
/// <param name="CreateView">
/// Builds the control for one placed instance, on the UI thread, handed that instance's own
/// <see cref="IWidgetContext"/> (per-instance storage for its config, the session-observe surface, a refresh
/// signal). Invoked once per instance; a widget that needs periodic updates owns its own timer or listens to
/// <see cref="IWidgetContext.RefreshRequested"/>.
/// </param>
public sealed record WidgetRegistration(string Id, string Title, Func<IWidgetContext, Control> CreateView)
{
    /// <summary>A short glyph/emoji shown on the gallery card and the pane header. Defaults to a neutral widget mark.</summary>
    public string Icon { get; init; } = "🧩";

    /// <summary>One line describing the widget for the gallery card. Empty by default.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>How many grid columns a freshly placed instance spans (the operator can resize afterwards). Defaults to 1.</summary>
    public int DefaultColumnSpan { get; init; } = 1;

    /// <summary>How many grid rows a freshly placed instance spans. Defaults to 1.</summary>
    public int DefaultRowSpan { get; init; } = 1;

    /// <summary>
    /// Builds this instance's settings form, or null when the widget has nothing to configure — a clock needs
    /// no settings, a system monitor picks its metrics. Null is not just "no form": it is what hides the ⚙ on
    /// the pane header (see <see cref="HasConfig"/>), so a widget can never show a gear that opens an empty
    /// dialog. Handed the same per-instance <see cref="IWidgetContext"/> as <see cref="CreateView"/>, so the
    /// form reads and writes the very config its view renders, through
    /// <see cref="IWidgetContext.Storage"/>.
    /// <para>
    /// The plugin supplies the form's content only — the host wraps it in the dialog with the Save/Close
    /// footer, exactly as it does for <c>AddSettings</c>. Saving raises
    /// <see cref="IWidgetContext.RefreshRequested"/> on that instance, so the view picks the new config up
    /// without the widget having to watch its own storage.
    /// </para>
    /// </summary>
    public Func<IWidgetContext, Control>? CreateConfigView { get; init; }

    /// <summary>
    /// Whether this widget has a settings form — the single fact the pane header's ⚙ is bound to. Derived from
    /// <see cref="CreateConfigView"/> rather than declared alongside it, so there is no flag that can claim
    /// settings the widget cannot build (the mistake a <c>SupportsConfig</c> bool invites).
    /// </summary>
    public bool HasConfig => CreateConfigView is not null;
}
