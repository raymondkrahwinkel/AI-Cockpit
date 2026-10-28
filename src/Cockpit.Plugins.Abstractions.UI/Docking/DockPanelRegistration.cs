using Avalonia.Controls;
using Material.Icons;

namespace Cockpit.Plugins.Abstractions.Docking;

/// <summary>
/// A panel a plugin contributes to the right-hand dock rail (<see cref="ICockpitHost.AddDockPanel"/>).
/// The rail owns width and collapse; this only says what tab to show and what to build behind it.
/// </summary>
/// <param name="Id">
/// A stable, namespaced id (e.g. "github.pull-requests") — persisted as the open panel across a
/// restart, so treat it as an API surface.
/// </param>
/// <param name="Title">
/// Shown as the vertical tab label on the collapsed rail.
/// </param>
/// <param name="IconKind">
/// Shown above the title on the rail tab; required, since the rail has no text fallback.
/// </param>
/// <param name="CreateView">
/// Builds the panel's content, on the UI thread: each time it is opened, or once when <see cref="KeepAlive"/> is set.
/// </param>
public sealed record DockPanelRegistration(string Id, string Title, MaterialIconKind IconKind, Func<Control> CreateView)
{
    /// <summary>
    /// An optional count on the tab, for what in the panel waits on the operator; null draws none.
    /// </summary>
    public DockPanelBadge? Badge { get; init; }

    /// <summary>
    /// Builds the view once and keeps that instance across collapsing the rail, switching tabs and moving between
    /// the rail and its own window — for a view whose state (a page, a playing video) must outlive being hidden.
    /// Host 0.111.0 and later; an older host ignores it and rebuilds on every opening.
    /// </summary>
    public bool KeepAlive { get; init; }

    /// <summary>
    /// Offers an undock button in the rail, which moves the panel into its own window and back. The stand and the
    /// window's bounds survive a restart. Combine with <see cref="KeepAlive"/> to move the same view instance.
    /// Host 0.111.0 and later; an older host ignores it.
    /// </summary>
    public bool CanFloat { get; init; }

    /// <summary>
    /// Buttons the host draws in the active dock panel's rail header, immediately left of undock. Empty by default.
    /// Host 0.133.0 and later; an older host ignores them.
    /// </summary>
    public IReadOnlyList<ToolbarAction> HeaderActions { get; init; } = [];
}
