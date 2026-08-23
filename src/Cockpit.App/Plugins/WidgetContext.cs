using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.Plugins;

// The host's `IWidgetContext`, built per instance. View and settings form share one instance, so
// a form write is picked up by the view via `RefreshRequested` without either side watching storage.
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
