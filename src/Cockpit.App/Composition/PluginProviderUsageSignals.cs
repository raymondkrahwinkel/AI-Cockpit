using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Composition;

internal sealed class PluginProviderUsageSignals(IPluginProviderRegistry registry) : IProviderUsageSignals, ISingletonService
{
    public IReadOnlyList<PluginUsageSignal>? UsageSignalsOf(string providerId) => registry.Resolve(providerId)?.UsageSignals;
}
