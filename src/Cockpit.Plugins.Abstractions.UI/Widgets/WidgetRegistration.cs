using Avalonia.Controls;
using Material.Icons;

namespace Cockpit.Plugins.Abstractions.Widgets;

/// <summary>
/// A dashboard widget type a plugin contributes (<see cref="ICockpitHost.AddWidget"/>). A Dashboard workspace
/// shows every registered type in its "Add widget" gallery; picking one creates an instance, and
/// <see cref="CreateView"/> builds that instance's control.
/// </summary>
/// <remarks>
/// The core stays unaware of what any widget shows, the same way it stays unaware of a provider's transcript
/// format. See docs/workspaces-widgets-terminals.md.
/// </remarks>
/// <param name="Id">
/// A stable, unique id for the widget <em>type</em>, namespaced by the plugin (e.g. "system-monitor.usage").
/// Persisted with each placed instance so a saved dashboard rebuilds after a restart; changing it orphans
/// existing instances, so treat it as an API surface.
/// </param>
/// <param name="Title">
/// The widget's display name, shown in the gallery and as the pane's default header.
/// </param>
/// <param name="CreateView">
/// Builds the control for one placed instance, on the UI thread, handed that instance's own
/// <see cref="IWidgetContext"/> (per-instance storage for its config, the session-observe surface, a refresh
/// signal). Invoked once per instance; a widget that needs periodic updates owns its own timer or listens to
/// <see cref="IWidgetContext.RefreshRequested"/>.
/// </param>
public sealed record WidgetRegistration(string Id, string Title, Func<IWidgetContext, Control> CreateView)
{
    /// <summary>
    /// A short glyph shown on the gallery card and the pane header when <see cref="IconKind"/> is null. Empty by default — every bundled widget sets <see cref="IconKind"/> instead; a plugin may still put an emoji or letter here.
    /// </summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>
    /// A bundled vector icon for the gallery card and pane header, preferred over <see cref="Icon"/> when set, so the
    /// widget reads as part of the theme instead of an emoji the host renders in the machine's own font. Null keeps
    /// the <see cref="Icon"/> string.
    /// </summary>
    public MaterialIconKind? IconKind { get; init; }

    /// <summary>
    /// One line describing the widget for the gallery card. Empty by default.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// How many grid columns a freshly placed instance spans (the operator can resize afterwards). Defaults to 1.
    /// </summary>
    public int DefaultColumnSpan { get; init; } = 1;

    /// <summary>
    /// How many grid rows a freshly placed instance spans. Defaults to 1.
    /// </summary>
    public int DefaultRowSpan { get; init; } = 1;

    /// <summary>
    /// Builds this instance's settings form, or null when the widget has nothing to configure. Null hides the ⚙
    /// on the pane header (see <see cref="HasConfig"/>).
    /// </summary>
    /// <remarks>
    /// Handed the same per-instance <see cref="IWidgetContext"/> as <see cref="CreateView"/>. The host wraps the
    /// form in the dialog with the Save/Close footer; saving raises
    /// <see cref="IWidgetContext.RefreshRequested"/> on that instance.
    /// </remarks>
    public Func<IWidgetContext, Control>? CreateConfigView { get; init; }

    /// <summary>
    /// Whether this widget has a settings form — the single fact the pane header's ⚙ is bound to. Derived from
    /// <see cref="CreateConfigView"/> rather than declared alongside it, so there is no flag that can claim
    /// settings the widget cannot build (the mistake a <c>SupportsConfig</c> bool invites).
    /// </summary>
    public bool HasConfig => CreateConfigView is not null;
}
