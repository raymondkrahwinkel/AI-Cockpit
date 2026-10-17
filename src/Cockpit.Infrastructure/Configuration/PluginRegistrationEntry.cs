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

    // Whether this plugin is pinned top-level in the sidebar (AC-937), or null when the operator has never
    // expressed a preference — see `ToDomain` for the one-time migration default that applies while null.
    public bool? PinnedToSidebar { get; set; }

    // AC-1500: what this build's bundle expects at least (null for a plugin not in the bundle), and whether the operator chose an older version on purpose.
    public string? ExpectedMinVersion { get; set; }

    public bool VersionChosen { get; set; }

    // The plugin's own key/value storage (`Cockpit.Plugins.Abstractions.IPluginStorage`); values are JSON strings. Owned by the plugin, not the load decision.
    public Dictionary<string, string> Data { get; set; } = [];

    public static PluginRegistrationEntry FromDomain(PluginRegistration registration) => new()
    {
        Enabled = registration.Enabled,
        PinnedSha256 = registration.PinnedSha256,
        MenuOrder = registration.MenuOrder,
        HiddenInMenu = registration.HiddenInMenu,
        PinnedToSidebar = registration.PinnedToSidebar,
        ExpectedMinVersion = registration.ExpectedMinVersion,
        VersionChosen = registration.VersionChosen,
    };

    // AC-937 (Raymond, voorstel A): every plugin starts collapsed into "Plugins ›" until the operator pins it.
    public PluginRegistration ToDomain() =>
        new(Enabled, PinnedSha256, MenuOrder, HiddenInMenu, PinnedToSidebar ?? false, ExpectedMinVersion, VersionChosen);
}
