using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;

namespace Cockpit.Plugin.Depot.Settings;

// The plugin's settings, persisted through the host's per-plugin `IPluginStorage` (AC-243) — the
// client-local (not synced) half of AC-242, same storage seam `KubernetesSettings` uses for its cluster list.
// Nothing here is secret (see `DepotConnectionRegistration`), so this is plain JSON metadata; the
// credential the host acquires for each contributed server lives in the host's own OAuth store, never in this
// plugin's storage. Read fresh on every access, so a settings save takes effect on the next call without a restart.
internal sealed class DepotSettings(IPluginStorage storage)
{
    // AC-499: a value saved before DepotUrlNormalizer existed (Depot's own docs tell the operator to paste the
    // full endpoint including /mcp) can still carry that trailing /mcp raw in storage. Cleaning it up cannot be
    // "normalize on every read": Normalize now strips exactly one trailing /mcp, not every one, so re-running it
    // on an already-clean base whose deployment path genuinely ends in /mcp (a legitimate, already-migrated value)
    // would strip that segment too — and there is no way to tell that case apart from a not-yet-migrated one by
    // looking at the string alone. This flag is what makes the migration below run exactly once, ever, per
    // installation, instead of once per read or once per app start.
    private const string UrlsNormalizedKey = "connectionsUrlsNormalizedAc499";
    private const string SettingsSyncMigratedKey = "settingsSyncMigratedAc1832";

    public IReadOnlyList<DepotConnectionRegistration> Connections
    {
        get
        {
            _MigrateStoredUrlsOnce();
            _MigrateSettingsSyncOnce();
            return storage.Get<List<DepotConnectionRegistration>>("connections") ?? [];
        }
        set
        {
            storage.Set("connections", value.ToList());
            storage.Set(SettingsSyncMigratedKey, true);
        }
    }

    // AC-1574: the folder-to-Depot links. A setting, so it goes with a backup; the manifest of what each one published is cache.
    public IReadOnlyList<DepotPublication> Publications
    {
        get => storage.Get<List<DepotPublication>>("publications") ?? [];
        set => storage.Set("publications", value.ToList());
    }

    private void _MigrateStoredUrlsOnce()
    {
        if (storage.Get<bool?>(UrlsNormalizedKey) ?? false)
        {
            return;
        }

        var stored = storage.Get<List<DepotConnectionRegistration>>("connections") ?? [];
        var migrated = stored.Select(connection => connection with { Url = DepotUrlNormalizer.Normalize(connection.Url) }).ToList();
        storage.Set("connections", migrated);
        storage.Set(UrlsNormalizedKey, true);
    }

    // AC-1832: old connection lists had no setting-sync choice and always used their first server.
    private void _MigrateSettingsSyncOnce()
    {
        if (storage.Get<bool?>(SettingsSyncMigratedKey) ?? false)
        {
            return;
        }

        var stored = storage.Get<List<DepotConnectionRegistration>>("connections") ?? [];
        storage.Set("connections", stored.Select((connection, index) => connection with { SynchroniseSettings = index == 0 }).ToList());
        storage.Set(SettingsSyncMigratedKey, true);
    }
}
