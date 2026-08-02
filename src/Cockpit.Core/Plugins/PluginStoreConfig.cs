using System.Text.Json.Serialization;

namespace Cockpit.Core.Plugins;

// A configured plugin store (#14, AC-7): a remote http(s) index or a local folder, plus an optional bearer
// `Token` for a private remote store. `Location` is the store URL (remote) or the
// folder path (local) — the one thing that identifies it.
//
// Persisted in `cockpit.json`'s `pluginStores`. It serialises to an object, but a bare string is
// still read as a public remote store, so a config written before AC-7 keeps working (see
// `PluginStoreConfigJsonConverter`). The token field is named to fall under the host's
// secret-field rule, so it is encrypted at rest and scrubbed from backups whenever protection is on.
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

    // Whether this and `other` point at the same store — identity is kind + location. A remote
    // location (a URL) compares case-insensitively as before; a local location is a filesystem path, compared
    // case-sensitively, so two genuinely different folders on a case-sensitive filesystem are never mistaken for
    // one — otherwise adding one could silently drop the other.
    public bool SameStoreAs(PluginStoreConfig other) =>
        Kind == other.Kind
        && string.Equals(Location, other.Location, IsLocal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // Overrides the record's auto-generated `ToString()`, which would otherwise print `Token` — a credential has no business in a log line (Iron Law #8).
    public override string ToString() =>
        $"{nameof(PluginStoreConfig)} {{ Kind = {Kind}, Location = {Location}, Token = {(HasToken ? "***" : "null")} }}";
}
