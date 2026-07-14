namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry in the plugin store dialog's (#62) sidebar: its display label and the
/// <see cref="PluginStoreFilter"/> selecting it applies. <see cref="IsEnabled"/> is only false for the
/// bottom "Installed"/"Available updates" entries when their count is zero — they still show ("(0)")
/// but are greyed out and unclickable rather than disappearing.
/// </summary>
public sealed record PluginStoreSidebarItem(string Label, PluginStoreFilter Filter, bool IsEnabled = true)
{
    public static PluginStoreSidebarItem Discover { get; } = new("Discover", PluginStoreFilter.Discover);

    public static PluginStoreSidebarItem All { get; } = new("All plugins", PluginStoreFilter.All);

    public static PluginStoreSidebarItem ForCategory(string category) => new(category, PluginStoreFilter.ForCategory(category));

    public static PluginStoreSidebarItem Templates(int count) => new($"Workflow templates ({count})", PluginStoreFilter.Templates, count > 0);

    public static PluginStoreSidebarItem Installed(int count) => new($"Installed ({count})", PluginStoreFilter.Installed, count > 0);

    public static PluginStoreSidebarItem UpdatesAvailable(int count) => new($"Available updates ({count})", PluginStoreFilter.UpdatesAvailable, count > 0);
}
