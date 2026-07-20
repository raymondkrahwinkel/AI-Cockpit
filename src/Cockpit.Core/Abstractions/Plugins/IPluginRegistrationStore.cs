using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Persists per-plugin enable + consent state (the <c>plugins</c> section of <c>cockpit.json</c>), keyed
/// by folder id. The plugin manager reads it to render the overview and writes it on enable/disable/
/// remove; the loader reads it to decide what to load at startup.
/// </summary>
public interface IPluginRegistrationStore
{
    Task<IReadOnlyDictionary<string, PluginRegistration>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists a plugin's enabled + pinned-hash state, preserving its stored <see cref="LoadDataAsync"/> key/value data and its left-menu preference.</summary>
    Task SaveAsync(string folderId, PluginRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists where this plugin's contributions sit in the left menu and whether they are shown at all (#72),
    /// leaving the enable/consent state and the plugin's own data untouched — a separate write, because moving a
    /// plugin down the menu says nothing about whether it may run.
    /// </summary>
    Task SaveMenuPreferenceAsync(string folderId, int menuOrder, bool hiddenInMenu, CancellationToken cancellationToken = default);

    Task RemoveAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The folder ids of bundled plugins already seeded at least once — the seed-once ledger the bundled
    /// installer measures "first appearance" against. Empty on a config that predates the ledger.
    /// </summary>
    Task<IReadOnlySet<string>> LoadSeededBundledIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the given bundled plugin ids have been seeded, so they are never auto-installed again — not
    /// after an operator uninstall, not to roll back a newer store version. Merged into the existing ledger;
    /// ids already present are a no-op. Leaves every other config section untouched.
    /// </summary>
    Task MarkBundledSeededAsync(IEnumerable<string> folderIds, CancellationToken cancellationToken = default);

    /// <summary>Loads a plugin's own key/value storage slice (its <see cref="Cockpit.Plugins.Abstractions.IPluginStorage"/> data); empty when the plugin has stored nothing.</summary>
    Task<IReadOnlyDictionary<string, string>> LoadDataAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>Persists a plugin's own key/value storage slice, preserving its enabled + pinned-hash state.</summary>
    Task SaveDataAsync(string folderId, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default);
}
