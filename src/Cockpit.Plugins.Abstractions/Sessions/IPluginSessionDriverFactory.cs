namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Creates the <see cref="IPluginSessionDriver"/> for one plugin-registered provider (#45), given the
/// profile's opaque config JSON (see <c>PluginProviderConfig.ConfigJson</c>, <c>Cockpit.Core.Profiles</c>) —
/// the plugin owns its own config record's shape and (de)serialization; the host never needs to know it.
/// </summary>
public interface IPluginSessionDriverFactory
{
    IPluginSessionDriver Create(string configJson);
}
