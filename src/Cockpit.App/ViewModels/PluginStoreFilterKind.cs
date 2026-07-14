namespace Cockpit.App.ViewModels;

/// <summary>The kind of scope a plugin store dialog (#62) sidebar selection narrows the catalogue to.</summary>
public enum PluginStoreFilterKind
{
    /// <summary>The Discover page: Featured + Recently-added rails, followed by the same grid as <see cref="All"/>.</summary>
    Discover,

    /// <summary>The full, unfiltered catalogue.</summary>
    All,

    /// <summary>Entries whose <see cref="StorePluginRowViewModel.Category"/> matches <see cref="PluginStoreFilter.Category"/>.</summary>
    Category,

    /// <summary>Entries already installed (<see cref="StorePluginRowViewModel.IsInstalled"/>).</summary>
    Installed,

    /// <summary>Installed entries with a newer version in the store (<see cref="StorePluginRowViewModel.UpdateAvailable"/>).</summary>
    UpdatesAvailable,

    /// <summary>
    /// The workflow templates the stores offer (#69) — flows somebody already drew. Their own scope, because they are
    /// not plugins: nothing is loaded, no code runs, and what you are agreeing to is the steps on your canvas. A
    /// section under the plugin grid is a place nobody scrolls to.
    /// </summary>
    Templates,
}
