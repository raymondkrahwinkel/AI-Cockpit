namespace Cockpit.Core.Profiles;

// Connection settings for a profile under a plugin-registered provider (#45): one generic case for every plugin
// provider, instead of a bespoke `ProviderConfig` record per plugin. `ProviderId` identifies the registered
// `SessionProviderRegistration`; `ConfigJson` is the plugin's own config, serialized to whatever JSON shape it chooses — the host never needs to know it.
public sealed record PluginProviderConfig(string ProviderId, string ConfigJson) : ProviderConfig(SessionProvider.Plugin);
