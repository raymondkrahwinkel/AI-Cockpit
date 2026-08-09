namespace Cockpit.Core.Plugins;

// One plugin advertised by a store: its identity, display fields, the latest version and the full
// version history, plus the optional presentation fields the store dialog (#62) uses for browsing —
// `Category`/`Icon`/`Homepage`/`Repository`/
// `Featured`/`Published` — and `WorkKind` (AC-511), the curator's own
// second axis. All seven are additive and default to "not set" (null/false), so an `index.json`
// published before they existed still parses without them: the store dialog falls back to an "Other"
// category, a monogram icon, no links, no Featured/Recently-added rail membership, and no work-kind
// recommendation. `Icon` is a single emoji/glyph, not an image path — see the #62 design doc
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
    // AC-511: a free string, not the domain's own enum — an unrecognised value would otherwise fail the whole
    // index (see PluginWorkKinds' own remarks), and the placeholder set is not settled yet either way.
    string? WorkKind = null,
    string? LogoAsset = null)
{
    // The `Category` value that marks a plugin as an AI provider (AC-510[b] criterion 5). Measured
    // against the default store's live `index.json` (raymondkrahwinkel/AI-Cockpit-Plugins, 2026-08-02):
    // exactly `claude-provider`, `cli-agent-provider`, `gemini-provider`,
    // `github-models-provider` and `kimi-provider` carry this category, and nothing else in the index
    // does — so, unlike `WorkKind` (which needed its own additive field for AC-511), "is this a
    // provider" is carried by the existing technique-shaped `Category` axis without adding one.
    public const string ProviderCategory = "AI providers";
}
