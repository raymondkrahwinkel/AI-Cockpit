using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Depot.Model;

namespace Cockpit.Plugin.Depot.Settings;

/// <summary>
/// The plugin's settings, persisted through the host's per-plugin <see cref="IPluginStorage"/> (AC-243) — the
/// client-local (not synced) half of AC-242, same storage seam <c>KubernetesSettings</c> uses for its cluster list.
/// Nothing here is secret (see <see cref="DepotConnectionRegistration"/>), so this is plain JSON metadata; the
/// credential the host acquires for each contributed server lives in the host's own OAuth store, never in this
/// plugin's storage. Read fresh on every access, so a settings save takes effect on the next call without a restart.
/// </summary>
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

    public IReadOnlyList<DepotConnectionRegistration> Connections
    {
        get
        {
            _MigrateStoredUrlsOnce();
            return storage.Get<List<DepotConnectionRegistration>>("connections") ?? [];
        }
        set => storage.Set("connections", value.ToList());
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
}
