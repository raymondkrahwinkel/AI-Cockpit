namespace Cockpit.App.ViewModels;

/// <summary>
/// The plugin store dialog's (#62) current sidebar scope: a <see cref="PluginStoreFilterKind"/> plus,
/// for <see cref="PluginStoreFilterKind.Category"/>, the category name. A record so two filters for the
/// same scope compare equal by value — used to re-find the matching sidebar item after the sidebar list
/// is rebuilt (categories are derived from the catalogue and can change between loads).
/// </summary>
public sealed record PluginStoreFilter(PluginStoreFilterKind Kind, string? Category = null)
{
    public static PluginStoreFilter Discover { get; } = new(PluginStoreFilterKind.Discover);

    public static PluginStoreFilter All { get; } = new(PluginStoreFilterKind.All);

    public static PluginStoreFilter Installed { get; } = new(PluginStoreFilterKind.Installed);

    public static PluginStoreFilter UpdatesAvailable { get; } = new(PluginStoreFilterKind.UpdatesAvailable);

    public static PluginStoreFilter ForCategory(string category) => new(PluginStoreFilterKind.Category, category);
}
