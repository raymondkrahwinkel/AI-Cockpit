namespace Cockpit.App.ViewModels;

// The kind of scope a plugin store dialog (#62) sidebar selection narrows the catalogue to.
public enum PluginStoreFilterKind
{
    // The Discover page: Featured + Recently-added rails, followed by the same grid as `All`.
    Discover,

    // The full, unfiltered catalogue.
    All,

    // Entries whose `StorePluginRowViewModel.Category` matches `PluginStoreFilter.Category`.
    Category,

    // Entries already installed (`StorePluginRowViewModel.IsInstalled`).
    Installed,

    // Installed entries with a newer version in the store (`StorePluginRowViewModel.UpdateAvailable`).
    UpdatesAvailable,

    // The workflow templates the stores offer (#69) — flows somebody already drew. Their own scope, because they are
    // not plugins: nothing is loaded, no code runs, and what you are agreeing to is the steps on your canvas. A
    // section under the plugin grid is a place nobody scrolls to.
    Templates,
}
