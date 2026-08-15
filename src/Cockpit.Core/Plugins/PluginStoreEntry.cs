namespace Cockpit.Core.Plugins;

// One plugin advertised by a store: its identity, display fields, the latest version and the full
// version history, plus the optional presentation fields the store dialog (#62) uses for browsing —
// `Category`/`Icon`/`Homepage`/`Repository`/
// `Featured`/`Published` — `Audience` (AC-511), the curator's own
// second axis — and `Hidden` (AC-815), which drops an entry out of the browsable store entirely. All
// are additive and default to "not set" (null/false/empty), so an `index.json` published before they existed
// still parses without them: the store dialog falls back to an "Other" category, a monogram icon, no
// links, no Featured/Recently-added rail membership, no audience recommendation, and every plugin
// browsable. `Icon` is a single emoji/glyph, not an image path — see the #62 design doc
// for why (no new download/cache layer needed for a text glyph the app already renders elsewhere, e.g.
// the titlebar caption glyphs).
//
// `LogoAsset` (AC-553) is additive too: a bare file name resolves against the host's bundled assets, an
// `http(s)` URL is fetched live from the vendor's own CDN (see NOTICE) — either falls back to `Icon`, then
// the monogram, when it does not resolve.
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
