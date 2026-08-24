namespace Cockpit.Core.Plugins;

// AC-1013: One plugin advertised by a store — identity, display fields, version history, plus optional
// presentation fields the store dialog (#62) uses for browsing. All are additive/default "not set" so
// a pre-existing `index.json` still parses (per-field fallback behavior: see AC-511, AC-815, AC-553).
public sealed record PluginStoreEntry(
    string Id,
    string Name,
    string? Description,
    string? Author,
    string LatestVersion,
    IReadOnlyList<PluginStoreVersion> Versions,
    string? Category = null,
    string? Icon = null,
    string? Homepage = null,
    string? Repository = null,
    bool Featured = false,
    string? Published = null,
    // AC-511 criterion 6: the work kinds a plugin is recommended for. A free list of strings, not the domain's
    // own enum — an unrecognised value still round-trips instead of failing the whole index. Null/empty means
    // generic: the wizard shows the plugin for every work kind chosen, not just a listed one.
    IReadOnlyList<string>? Audience = null,
    string? LogoAsset = null,
    // AC-815: hides the entry from Discover/All/categories/search/Featured/Recently-added — install-from-zip
    // does not consult the index, so it is unaffected.
    bool Hidden = false)
{
    // The `Category` value marking a plugin as an AI provider (AC-510[b] criterion 5): exactly the five provider
    // plugins carry it in the live index, so "is this a provider" reuses `Category` rather than its own field.
    public const string ProviderCategory = "AI providers";
}
