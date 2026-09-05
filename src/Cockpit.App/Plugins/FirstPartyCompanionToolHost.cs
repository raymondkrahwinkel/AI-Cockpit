using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

internal sealed class FirstPartyCompanionToolHost(
    ICompanionToolRegistry registry,
    IPluginStorage storage,
    ICockpitSessionObserver sessions)
{
    public bool AddCompanionTool(CompanionToolRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return registry.Register(registration, storage, sessions);
    }
}
