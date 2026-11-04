using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Infrastructure.Plugins;

// A singleton of its own so a backend without plugins resolves the catalog too; PluginManager's replaces it when loading.
public sealed class PluginMcpProviderRegistry(params IPluginMcpProvider[] providers) : ISingletonService
{
    public PluginMcpProviderRegistry()
        : this([])
    {
    }

    private readonly object _gate = new();
    private readonly List<IPluginMcpProvider> _providers = [.. providers];

    public IReadOnlyList<IPluginMcpProvider> Providers
    {
        get
        {
            lock (_gate)
            {
                return [.. _providers];
            }
        }
    }

    public void Register(IPluginMcpProvider provider)
    {
        lock (_gate)
        {
            if (!_providers.Any(existing => ReferenceEquals(existing, provider)))
            {
                _providers.Add(provider);
            }
        }
    }

    internal void Remove(IPluginMcpProvider provider)
    {
        lock (_gate)
        {
            _providers.Remove(provider);
        }
    }
}
