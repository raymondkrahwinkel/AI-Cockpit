using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.Plugins;

/// <summary>
/// The host's <see cref="IWidgetContext"/>: what one placed widget is handed, built per instance so its
/// storage and its refresh signal are its own. The view and the settings form get the same instance, which
/// is what lets a form write config the view then re-reads on <see cref="RefreshRequested"/> without either
/// side watching storage.
/// </summary>
public sealed class WidgetContext(string instanceId, IPluginStorage pluginStorage, ICockpitSessionObserver sessions) : IWidgetContext
{
    public string InstanceId => instanceId;

    public IPluginStorage Storage { get; } = new WidgetInstanceStorage(pluginStorage, instanceId);

    public ICockpitSessionObserver Sessions => sessions;

    public event EventHandler? RefreshRequested;

    /// <summary>
    /// Asks this instance to re-read and update — raised by the pane's ↻ and after its settings form saves.
    /// Host-side only: a widget listens to <see cref="RefreshRequested"/>, it does not fire it.
    /// </summary>
    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);
}
