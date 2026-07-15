using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.Abstractions.Widgets;

/// <summary>
/// Handed to a widget instance's view factory (<see cref="WidgetRegistration.CreateView"/>): everything one
/// placed widget needs and nothing it does not. Per-instance so two "System Monitor" widgets on the same
/// dashboard keep separate config, and so a widget can react to what the cockpit is doing (a git widget
/// following the active session's working directory) without the core knowing what the widget is.
/// </summary>
public interface IWidgetContext
{
    /// <summary>This placed instance's stable id — distinct from the widget <em>type</em> id, and the key its config is stored under.</summary>
    string InstanceId { get; }

    /// <summary>
    /// Per-instance persistence for this widget's own config (which metrics to show, a chosen repo, scratch text).
    /// Scoped to <see cref="InstanceId"/> under the owning plugin's storage, so it survives a restart and never
    /// collides with another instance of the same widget.
    /// </summary>
    IPluginStorage Storage { get; }

    /// <summary>
    /// The same read/observe surface over the cockpit's sessions the host exposes (<see cref="ICockpitHost.Sessions"/>):
    /// the active session's working directory and its output stream, so a widget can follow what a session is doing.
    /// </summary>
    ICockpitSessionObserver Sessions { get; }

    /// <summary>
    /// Raised when the host asks this instance to refresh — the pane's refresh control, or a dashboard-wide refresh.
    /// A widget that polls on its own timer can ignore this; one that shows a snapshot should re-read and update.
    /// </summary>
    event EventHandler RefreshRequested;
}
