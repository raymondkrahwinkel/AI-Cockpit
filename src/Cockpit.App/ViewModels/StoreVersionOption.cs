using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry in the plugin store's version-picker dropdown (#: version rollback): a store <see cref="Version"/>
/// and whether it is the one currently installed, so the dropdown can mark it and the install button can read
/// "Reinstall" instead of "Install".
/// </summary>
public sealed record StoreVersionOption(PluginStoreVersion Version, bool IsInstalled)
{
    public string Display => IsInstalled ? $"v{Version.Version} · installed" : $"v{Version.Version}";
}
