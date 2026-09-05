using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.Abstractions.CompanionTools;

/// <summary>
/// Handed to a companion tool's view factory (<see cref="CompanionToolRegistration.CreateView"/>): everything one
/// tool needs and nothing it does not — the same role <see cref="Cockpit.Plugins.Abstractions.Widgets.IWidgetContext"/>
/// plays for a placed widget instance.
/// </summary>
public interface ICompanionToolContext
{
    /// <summary>
    /// The same read/observe surface over the cockpit's sessions the host exposes (<see cref="ICockpitHost.Sessions"/>):
    /// the active session's working directory and its output stream, so a tool can follow what a session is doing.
    /// </summary>
    ICockpitSessionObserver SelectedSession { get; }

    /// <summary>
    /// Per-tool persistence for this companion tool's own state, so it survives a restart and never collides
    /// with another tool's state.
    /// </summary>
    IPluginStorage Storage { get; }

    /// <summary>
    /// Raised when the host asks this tool to refresh. A tool that polls on its own timer can ignore this; one
    /// that shows a snapshot should re-read and update.
    /// </summary>
    event EventHandler RefreshRequested;
}
