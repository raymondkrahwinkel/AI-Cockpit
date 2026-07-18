using System.Text.Json.Serialization;

namespace Cockpit.Core.Plugins;

/// <summary>
/// A configured plugin store (#14, AC-7): a remote http(s) index or a local folder, plus an optional bearer
/// <see cref="Token"/> for a private remote store. <see cref="Location"/> is the store URL (remote) or the
/// folder path (local) — the one thing that identifies it.
/// <para>
/// Persisted in <c>cockpit.json</c>'s <c>pluginStores</c>. It serialises to an object, but a bare string is
/// still read as a public remote store, so a config written before AC-7 keeps working (see
/// <see cref="PluginStoreConfigJsonConverter"/>). The token field is named to fall under the host's
/// secret-field rule, so it is encrypted at rest and scrubbed from backups whenever protection is on.
/// </para>
/// </summary>
[JsonConverter(typeof(PluginStoreConfigJsonConverter))]
public sealed record PluginStoreConfig(PluginStoreKind Kind, string Location, string? Token = null)
{
    /// <summary>A remote http(s) store, optionally private (a bearer <paramref name="token"/>).</summary>
    public static PluginStoreConfig Remote(string url, string? token = null) =>
        new(PluginStoreKind.Remote, url, string.IsNullOrWhiteSpace(token) ? null : token);

    /// <summary>A local folder holding an <c>index.json</c>.</summary>
    public static PluginStoreConfig Local(string path) => new(PluginStoreKind.Local, path);

    [JsonIgnore]
    public bool IsLocal => Kind == PluginStoreKind.Local;

    [JsonIgnore]
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    /// <summary>Whether this and <paramref name="other"/> point at the same store — identity is kind + location, case-insensitive.</summary>
    public bool SameStoreAs(PluginStoreConfig other) =>
        Kind == other.Kind && string.Equals(Location, other.Location, StringComparison.OrdinalIgnoreCase);

    /// <summary>Overrides the record's auto-generated <c>ToString()</c>, which would otherwise print <see cref="Token"/> — a credential has no business in a log line (Iron Law #8).</summary>
    public override string ToString() =>
        $"{nameof(PluginStoreConfig)} {{ Kind = {Kind}, Location = {Location}, Token = {(HasToken ? "***" : "null")} }}";
}
