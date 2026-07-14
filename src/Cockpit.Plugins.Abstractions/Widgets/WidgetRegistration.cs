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
}
