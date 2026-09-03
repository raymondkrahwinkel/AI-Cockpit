using Cockpit.Core.Plugins;

namespace Cockpit.Core.Backup;

// AC-1276: one installed plugin as what it takes to fetch it again, not the megabytes it unpacked to. The id
// alone does not identify it — the same id can sit in more than one store — so the store travels with it, and
// the version carries what the provisioner checks before downloading.
public sealed record BackupPluginIndexEntry(
    string Id,
    PluginStoreKind StoreKind,
    string StoreLocation,
    string Version,
    string? Path,
    int? AbstractionsVersion,
    string? MinHostVersion,
    string? Sha256)
{
    // The plugin's settings are not in the index: those are a `PluginRegistrationEntry` in `cockpit.json` and
    // come back with the settings. A store's `Token` is a credential and has no business in an archive the
    // operator may hand to someone else, so this factory — which cannot carry one — is how an entry is built.
    public static BackupPluginIndexEntry From(string id, PluginStoreConfig store, PluginStoreVersion version) =>
        new(
            id,
            store.Kind,
            store.Location,
            version.Version,
            version.Path,
            version.AbstractionsVersion,
            version.MinHostVersion,
            version.Sha256);
}
