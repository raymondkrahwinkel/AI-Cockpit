namespace Cockpit.Core.Profiles;

/// <summary>
/// Connection settings for a profile running under a plugin-registered provider (#45): one generic case for
/// every plugin provider, instead of a bespoke <see cref="ProviderConfig"/> record per plugin. <see cref="ProviderId"/>
/// identifies which registered <c>SessionProviderRegistration</c> (see <c>Cockpit.Infrastructure.Sessions.IPluginProviderRegistry</c>)
/// drives this profile; <see cref="ConfigJson"/> is the plugin's own config record, serialized to whatever
/// JSON shape it chooses — the host never needs to know that shape, only the plugin's own
/// <c>IPluginSessionDriverFactory</c>/<c>IPluginProviderConfigView</c> (de)serialize it.
/// </summary>
/// <param name="ProviderId">The registered provider's stable id, e.g. <c>"gemini-provider.gemini"</c>.</param>
/// <param name="ConfigJson">The plugin's own config record, serialized as JSON.</param>
public sealed record PluginProviderConfig(string ProviderId, string ConfigJson) : ProviderConfig(SessionProvider.Plugin);
