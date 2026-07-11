namespace Cockpit.App.ViewModels;

/// <summary>The plugin store dialog's (#62) sort order for the currently filtered catalogue.</summary>
public enum PluginStoreSortMode
{
    /// <summary>Alphabetical by name — the default.</summary>
    NameAscending,

    /// <summary>By <see cref="StorePluginRowViewModel.PublishedDate"/> descending; entries without a date sort last, by name.</summary>
    RecentlyUpdated,

    /// <summary>Alphabetical by author, then by name.</summary>
    Author,
}
