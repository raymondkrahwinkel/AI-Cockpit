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

    // The plugin's own key/value storage (`Cockpit.Plugins.Abstractions.IPluginStorage`); values are JSON strings. Owned by the plugin, not the load decision.
    public Dictionary<string, string> Data { get; set; } = [];

    public static PluginRegistrationEntry FromDomain(PluginRegistration registration) => new()
    {
        Enabled = registration.Enabled,
        PinnedSha256 = registration.PinnedSha256,
        MenuOrder = registration.MenuOrder,
        HiddenInMenu = registration.HiddenInMenu,
        PinnedToSidebar = registration.PinnedToSidebar,
    };

    // AC-937 (Raymond, voorstel B): Autopilot and Open PRs start pinned top-level in the sidebar; every other
    // plugin starts collapsed into "Plugins ›" until the operator pins it. Applies only while `PinnedToSidebar`
    // is null on disk — a preference the operator later set, even back to false, stays as they left it.
    private static readonly HashSet<string> DefaultPinnedFolderIds = new(StringComparer.Ordinal) { "autopilot", "github-pull-requests" };

    public PluginRegistration ToDomain(string folderId) =>
        new(Enabled, PinnedSha256, MenuOrder, HiddenInMenu, PinnedToSidebar ?? DefaultPinnedFolderIds.Contains(folderId));
}
