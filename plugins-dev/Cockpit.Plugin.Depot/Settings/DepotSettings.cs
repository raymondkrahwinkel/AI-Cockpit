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
    public IReadOnlyList<DepotConnectionRegistration> Connections
    {
        get => storage.Get<List<DepotConnectionRegistration>>("connections") ?? [];
        set => storage.Set("connections", value.ToList());
    }
}
