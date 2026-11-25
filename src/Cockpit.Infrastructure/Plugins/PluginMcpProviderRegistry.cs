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
    private readonly List<(IPluginMcpProvider Provider, string? PluginId)> _providers =
        [.. providers.Select(provider => (provider, (string?)null))];

    public IReadOnlyList<IPluginMcpProvider> Providers => [.. Registrations.Select(entry => entry.Provider)];

    internal IReadOnlyList<(IPluginMcpProvider Provider, string? PluginId)> Registrations
    {
        get
        {
            lock (_gate)
            {
                return [.. _providers];
            }
        }
    }

    public void Register(IPluginMcpProvider provider) => Register(provider, null);

    internal void Register(IPluginMcpProvider provider, string? pluginId)
    {
        lock (_gate)
        {
            if (!_providers.Any(existing => ReferenceEquals(existing.Provider, provider)))
            {
                _providers.Add((provider, pluginId));
            }
        }
    }

    internal void Remove(IPluginMcpProvider provider)
    {
        lock (_gate)
        {
            _providers.RemoveAll(entry => ReferenceEquals(entry.Provider, provider));
        }
    }
}
