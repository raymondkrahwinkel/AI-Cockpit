using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.Plugins;

// The host's `IWidgetContext`: what one placed widget is handed, built per instance so its
// storage and its refresh signal are its own. The view and the settings form get the same instance, which
// is what lets a form write config the view then re-reads on `RefreshRequested` without either
// side watching storage.
public sealed class WidgetContext(string instanceId, IPluginStorage pluginStorage, ICockpitSessionObserver sessions) : IWidgetContext
{
    public string InstanceId => instanceId;

    public IPluginStorage Storage { get; } = new WidgetInstanceStorage(pluginStorage, instanceId);

    public ICockpitSessionObserver Sessions => sessions;

    public event EventHandler? RefreshRequested;

    // Asks this instance to re-read and update — raised by the pane's ↻ and after its settings form saves.
    // Host-side only: a widget listens to `RefreshRequested`, it does not fire it.
    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);
}
