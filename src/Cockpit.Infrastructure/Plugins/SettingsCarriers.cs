using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.Settings;

namespace Cockpit.Infrastructure.Plugins;

// AC-540: the carrier a plugin registered (ICockpitHost.AddSettingsCarrier). Null without one, which is a cockpit
// whose settings stay on this machine. One carrier: a second has nothing to decide between, so it is ignored.
public sealed class SettingsCarriers(ILogger<SettingsCarriers> logger) : ISingletonService
{
    private SettingsCarrierRegistration? _carrier;

    public SettingsCarrierRegistration? Carrier => Volatile.Read(ref _carrier);

    public bool Add(string pluginId, SettingsCarrierRegistration registration)
    {
        if (Interlocked.CompareExchange(ref _carrier, registration, null) is { } kept)
        {
            logger.LogWarning("Settings carrier '{SettingsCarrier}' of plugin {PluginId} is ignored; '{KeptCarrier}' is already registered", registration.Id, pluginId, kept.Id);
            return false;
        }

        return true;
    }
}
