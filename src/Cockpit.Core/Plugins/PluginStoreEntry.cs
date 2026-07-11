namespace Cockpit.Core.Plugins;

/// <summary>
/// One plugin advertised by a store: its identity, display fields, the latest version and the full
/// version history, plus the optional presentation fields the store dialog (#62) uses for browsing —
/// <see cref="Category"/>/<see cref="Icon"/>/<see cref="Homepage"/>/<see cref="Repository"/>/
/// <see cref="Featured"/>/<see cref="Published"/>. All six are additive and default to "not set"
/// (null/false), so an <c>index.json</c> published before #62 still parses without them: the store
/// dialog then falls back to an "Other" category, a monogram icon, no links, and no Featured/Recently-
/// added rail membership. <see cref="Icon"/> is a single emoji/glyph, not an image path — see the #62
/// design doc for why (no new download/cache layer needed for a text glyph the app already renders
/// elsewhere, e.g. the titlebar caption glyphs).
/// </summary>
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
    string? Published = null);
