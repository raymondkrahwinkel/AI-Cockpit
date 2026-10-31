using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Settings;

namespace Cockpit.Infrastructure.Plugins;

public sealed class SettingsCarriers(ILogger<SettingsCarriers> logger) : ISingletonService
{
    private readonly ConcurrentDictionary<string, SettingsCarrierRegistration> _carriers = new(StringComparer.Ordinal);
    private SettingsCarrierRegistration? _carrier;
    private string? _pluginId;

    public SettingsCarrierRegistration? Carrier => Volatile.Read(ref _carrier);

    public IReadOnlyCollection<SettingsCarrierRegistration> All => _carriers.Values.ToArray();

    // AC-1663: the plugin that registered the carrier.
    internal string? PluginId => Volatile.Read(ref _pluginId);

    public bool Add(string pluginId, SettingsCarrierRegistration registration)
    {
        if (!_carriers.TryAdd(registration.Id, registration))
        {
            logger.LogWarning("Settings carrier '{SettingsCarrier}' of plugin {PluginId} is ignored because that id is already registered", registration.Id, pluginId);
            return false;
        }

        if (Interlocked.CompareExchange(ref _carrier, registration, null) is null)
        {
            Volatile.Write(ref _pluginId, pluginId);
        }
        return true;
    }
}
