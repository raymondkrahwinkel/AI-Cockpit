namespace Cockpit.App.ViewModels;

/// <summary>A labelled <see cref="PluginStoreSortMode"/> choice for the store dialog's sort picker.</summary>
public sealed record PluginStoreSortModeOption(string Label, PluginStoreSortMode Mode)
{
    public static IReadOnlyList<PluginStoreSortModeOption> All { get; } =
    [
        new("Name A–Z", PluginStoreSortMode.NameAscending),
        new("Recently updated", PluginStoreSortMode.RecentlyUpdated),
        new("Author", PluginStoreSortMode.Author),
    ];
}
