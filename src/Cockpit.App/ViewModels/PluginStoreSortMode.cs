namespace Cockpit.App.ViewModels;

// The plugin store dialog's (#62) sort order for the currently filtered catalogue.
public enum PluginStoreSortMode
{
    // Alphabetical by name — the default.
    NameAscending,

    // By `StorePluginRowViewModel.PublishedDate` descending; entries without a date sort last, by name.
    RecentlyUpdated,

    // Alphabetical by author, then by name.
    Author,
}
