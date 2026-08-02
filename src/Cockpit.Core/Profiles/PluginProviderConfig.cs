namespace Cockpit.Core.Profiles;

// Connection settings for a profile running under a plugin-registered provider (#45): one generic case for
// every plugin provider, instead of a bespoke `ProviderConfig` record per plugin. `ProviderId`
// identifies which registered `SessionProviderRegistration` (see `Cockpit.Infrastructure.Sessions.IPluginProviderRegistry`)
// drives this profile; `ConfigJson` is the plugin's own config record, serialized to whatever
// JSON shape it chooses — the host never needs to know that shape, only the plugin's own
// `IPluginSessionDriverFactory`/`IPluginProviderConfigView` (de)serialize it.
//
// `ProviderId`: The registered provider's stable id, e.g. `"gemini-provider.gemini"`.
// `ConfigJson`: The plugin's own config record, serialized as JSON.
public sealed record PluginProviderConfig(string ProviderId, string ConfigJson) : ProviderConfig(SessionProvider.Plugin);
