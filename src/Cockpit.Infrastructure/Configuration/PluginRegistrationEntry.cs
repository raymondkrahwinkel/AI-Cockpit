using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `PluginRegistration` in the `plugins` section of `cockpit.json`.
internal sealed class PluginRegistrationEntry
{
    public bool Enabled { get; set; }

    public string PinnedSha256 { get; set; } = "";

    // Position in the left menu (#72), low first; 0 for a plugin the operator never moved.
    public int MenuOrder { get; set; }

    // Whether the plugin's left-menu contributions are hidden while the plugin keeps running (#72).
    public bool HiddenInMenu { get; set; }

    // The plugin's own key/value storage (`Cockpit.Plugins.Abstractions.IPluginStorage`); values are JSON strings. Owned by the plugin, not the load decision.
    public Dictionary<string, string> Data { get; set; } = [];

    public static PluginRegistrationEntry FromDomain(PluginRegistration registration) => new()
    {
        Enabled = registration.Enabled,
        PinnedSha256 = registration.PinnedSha256,
        MenuOrder = registration.MenuOrder,
        HiddenInMenu = registration.HiddenInMenu,
    };

    public PluginRegistration ToDomain() => new(Enabled, PinnedSha256, MenuOrder, HiddenInMenu);
}
