namespace Cockpit.App.ViewModels;

// The plugin store dialog's (#62) current sidebar scope: a `PluginStoreFilterKind` plus, for
// `PluginStoreFilterKind.Category`, the category name.
public sealed record PluginStoreFilter(PluginStoreFilterKind Kind, string? Category = null)
{
    public static PluginStoreFilter Discover { get; } = new(PluginStoreFilterKind.Discover);

    public static PluginStoreFilter All { get; } = new(PluginStoreFilterKind.All);

    public static PluginStoreFilter Installed { get; } = new(PluginStoreFilterKind.Installed);

    public static PluginStoreFilter UpdatesAvailable { get; } = new(PluginStoreFilterKind.UpdatesAvailable);

    public static PluginStoreFilter Templates { get; } = new(PluginStoreFilterKind.Templates);

    public static PluginStoreFilter ForCategory(string category) => new(PluginStoreFilterKind.Category, category);
}
