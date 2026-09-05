using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

// The host's `ICompanionToolContext`, built by `CompanionToolRegistry.CreateContext` from the registering
// plugin's own storage/sessions — the same pair `WidgetContext` gets, so a tool's saved state survives a
// restart and `SelectedSession` reflects a real session, exactly as the interface's own doc promises.
internal sealed class CompanionToolContext(IPluginStorage storage, ICockpitSessionObserver sessions) : ICompanionToolContext
{
    public ICockpitSessionObserver SelectedSession => sessions;

    public IPluginStorage Storage { get; } = storage;

    public event EventHandler? RefreshRequested;

    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);
}
