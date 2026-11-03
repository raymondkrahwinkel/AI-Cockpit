using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Infrastructure.Plugins;

public sealed class PluginMcpProviderRegistry(params IPluginMcpProvider[] providers)
{
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
