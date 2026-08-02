namespace Cockpit.App.ViewModels;

// A labelled `PluginStoreSortMode` choice for the store dialog's sort picker.
public sealed record PluginStoreSortModeOption(string Label, PluginStoreSortMode Mode)
{
    public static IReadOnlyList<PluginStoreSortModeOption> All { get; } =
    [
        new("Name A–Z", PluginStoreSortMode.NameAscending),
        new("Recently updated", PluginStoreSortMode.RecentlyUpdated),
        new("Author", PluginStoreSortMode.Author),
    ];
}
