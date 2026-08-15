namespace Cockpit.Core.Plugins;

// One plugin advertised by a store: identity, versions, and the optional presentation fields the store dialog
// and wizard use — Category/Icon/Homepage/Repository/Featured/Published/Audience/LogoAsset (docs/plugins/
// PLUGIN-SDK.md). All are additive, defaulting to null/false/empty, so a pre-existing `index.json` still parses.
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
    string? LogoAsset = null)
{
    // The `Category` value marking a plugin as an AI provider (AC-510[b] criterion 5): exactly the five provider
    // plugins carry it in the live index, so "is this a provider" reuses `Category` rather than its own field.
    public const string ProviderCategory = "AI providers";
}
