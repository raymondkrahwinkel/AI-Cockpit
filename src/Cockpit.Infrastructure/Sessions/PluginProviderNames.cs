using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1449: the registry's display names, for a frontend that may not see the registry's plugin types.
internal sealed class PluginProviderNames(IPluginProviderRegistry registry) : ISessionProviderNames, ISingletonService
{
    public string? DisplayNameOf(string providerId) => registry.Resolve(providerId)?.DisplayName;
}
