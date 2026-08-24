using System.Text.Json.Serialization;

namespace Cockpit.Core.Plugins;

// AC-1013: A configured plugin store (#14, AC-7) — remote http(s) index or local folder, with an
// optional bearer `Token`; persisted in `cockpit.json`'s `pluginStores`, serialising a bare string
// (pre-AC-7 format) as a public remote store via `PluginStoreConfigJsonConverter`.
[JsonConverter(typeof(PluginStoreConfigJsonConverter))]
public sealed record PluginStoreConfig(PluginStoreKind Kind, string Location, string? Token = null)
{
    // A remote http(s) store, optionally private (a bearer `token`).
    public static PluginStoreConfig Remote(string url, string? token = null) =>
        new(PluginStoreKind.Remote, url, string.IsNullOrWhiteSpace(token) ? null : token);

    // A local folder holding an `index.json`.
    public static PluginStoreConfig Local(string path) => new(PluginStoreKind.Local, path);

    [JsonIgnore]
    public bool IsLocal => Kind == PluginStoreKind.Local;

    [JsonIgnore]
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    // AC-1013: Whether this and `other` are the same store (kind + location); a remote URL compares
    // case-insensitively, a local path case-sensitively so distinct folders are never merged.
    public bool SameStoreAs(PluginStoreConfig other) =>
        Kind == other.Kind
        && string.Equals(Location, other.Location, IsLocal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // Overrides the record's auto-generated `ToString()`, which would otherwise print `Token` — a credential has no business in a log line (Iron Law #8).
    public override string ToString() =>
        $"{nameof(PluginStoreConfig)} {{ Kind = {Kind}, Location = {Location}, Token = {(HasToken ? "***" : "null")} }}";
}
