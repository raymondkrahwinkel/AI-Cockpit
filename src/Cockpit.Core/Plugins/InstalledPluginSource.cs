namespace Cockpit.Core.Plugins;

// AC-1981: what the Installed tab says about where an installed plugin came from; every field is null when there is nothing to say.
public sealed record InstalledPluginSource(
    string? SourceLine = null,
    string? PublisherLine = null,
    string? SourceNotice = null,
    string? VersionNotice = null,
    string? RevokedNotice = null,
    string? UpdateVersion = null)
{
    public static InstalledPluginSource None { get; } = new();

    // `offers` is null until the stores were browsed: the source line and the unavailable notice need no fetch, the rest does.
    public static InstalledPluginSource Build(
        string? recordedSource,
        string installedVersion,
        IReadOnlyList<PluginStoreSourceInfo> sources,
        IReadOnlyList<InstalledPluginOffer>? offers,
        PluginTrustEntry? trust)
    {
        var bound = PluginSourceBinding.Resolve(recordedSource, (offers ?? []).Select(offer => offer.Store.SourceKey));
        if (bound is null)
        {
            return None;
        }

        var info = sources.FirstOrDefault(source => source.Store.SourceKey == bound);
        var offer = offers?.FirstOrDefault(candidate => candidate.Store.SourceKey == bound);
        var unavailable = info is null || info.IsWithdrawn;
        var versions = offer?.Entry.Versions;
        var gone = versions is { Count: > 0 } && !versions.Any(version => string.Equals(version.Version, installedVersion, StringComparison.OrdinalIgnoreCase));
        var fingerprint = trust?.KeyFingerprint;
        var keyName = offer?.Entry.Versions?.Select(version => version.PackageFor()).FirstOrDefault(package => package?.KeyFingerprint == fingerprint)?.KeyName;

        return new InstalledPluginSource(
            SourceLine: info is null ? PluginSourceNotices.Describe(bound) : PluginSourceNotices.Title(info),
            PublisherLine: fingerprint is null ? null : $"{(string.IsNullOrEmpty(keyName) ? "Signed" : $"Signed by {keyName}")} · {fingerprint}",
            SourceNotice: unavailable ? PluginSourceNotices.Unavailable : null,
            VersionNotice: gone ? PluginSourceNotices.VersionCleanedUp : null,
            RevokedNotice: fingerprint is not null && offer?.RevokedKeys?.Any(key => key.Fingerprint == fingerprint) == true
                ? PluginSourceNotices.RevokedPublisher(fingerprint)
                : null,
            UpdateVersion: gone && offer is not null && PluginVersion.IsNewer(offer.Entry.LatestVersion, installedVersion) ? offer.Entry.LatestVersion : null);
    }

    public bool HasPublisher => PublisherLine is not null;

    public bool HasSourceNotice => SourceNotice is not null;

    public bool HasVersionNotice => VersionNotice is not null;

    public bool HasRevokedNotice => RevokedNotice is not null;

    public bool HasUpdate => UpdateVersion is not null;

    public string UpdateLabel => $"Update to v{UpdateVersion}";
}

// One store's entry for an installed plugin, as the last browse saw it.
public sealed record InstalledPluginOffer(PluginStoreConfig Store, PluginStoreEntry Entry, IReadOnlyList<PluginStoreRevokedKey>? RevokedKeys);

public static class PluginSourceNotices
{
    public const string Unavailable = "This source is no longer available. The plugin keeps running without updates; you can still disable or remove it.";

    public const string VersionCleanedUp = "This version is no longer in the store. It keeps running, but it cannot be installed again.";

    public static string RevokedPublisher(string fingerprint) =>
        $"The publisher key ({fingerprint}) has been revoked. The plugin keeps running; installation and updates under this key are blocked.";

    // A store's name for people: the organization and its Depot for a signed source, else the name its index gave, else derived from the location.
    public static string Title(PluginStoreSourceInfo info) => info.Store.Signed is { } signed && Uri.TryCreate(info.Store.Location, UriKind.Absolute, out var location)
        ? $"{signed.Organization}, on {location.Host}"
        : info.Name ?? (info.Store.IsLocal ? Path.GetFileName(info.Store.Location.TrimEnd('/', '\\')) : PluginStoreUrl.DeriveDisplayName(info.Store.Location));

    // A source key for a source this cockpit no longer knows: its location when it records one, else a plain word.
    public static string Describe(string sourceKey) => sourceKey switch
    {
        _ when sourceKey.StartsWith("remote:", StringComparison.Ordinal) => PluginStoreUrl.DeriveDisplayName(sourceKey["remote:".Length..]),
        _ when sourceKey.StartsWith("local:", StringComparison.Ordinal) => $"the local store '{sourceKey["local:".Length..]}'",
        _ => "an organization store",
    };
}
