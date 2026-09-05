using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Depot;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Depot;

// Persists the Depot-mirror registry under the `depotMirrors` section of `cockpit.json` (AC-278), going
// through `CockpitConfigFileAccess` so each mutation is a gated read-modify-write that never clobbers a
// sibling section — the same seam the clone and worktree registries use.
internal sealed class DepotMirrorRegistryStore : IDepotMirrorRegistry, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public DepotMirrorRegistryStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the registry at an arbitrary config file path.
    internal DepotMirrorRegistryStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyList<DepotMirror>> ListAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile is null)
        {
            return [];
        }

        return configFile.DepotMirrors.Select(entry => entry.ToDomain()).ToList();
    }

    public Task AddAsync(DepotMirror record, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file =>
            {
                file.DepotMirrors.RemoveAll(entry => _SameKey(entry, record.InstanceHost, record.Slug));
                file.DepotMirrors.Add(DepotMirrorEntry.FromDomain(record));
            },
            cancellationToken);

    public Task RemoveAsync(string instanceHost, string slug, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.DepotMirrors.RemoveAll(entry => _SameKey(entry, instanceHost, slug)),
            cancellationToken);

    private static bool _SameKey(DepotMirrorEntry entry, string instanceHost, string slug) =>
        string.Equals(entry.InstanceHost, instanceHost, StringComparison.Ordinal)
        && string.Equals(entry.Slug, slug, StringComparison.Ordinal);
}
