using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Clones;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Clones;

/// <summary>
/// Persists the repository-clone registry under the <c>clones</c> section of <c>cockpit.json</c> (AC-90), going
/// through <see cref="CockpitConfigFileAccess"/> so each mutation is a gated read-modify-write that never clobbers a
/// sibling section — the same seam the worktree registry and the settings stores use.
/// </summary>
internal sealed class RepositoryCloneRegistryStore : IRepositoryCloneRegistry, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public RepositoryCloneRegistryStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the registry at an arbitrary config file path.</summary>
    internal RepositoryCloneRegistryStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyList<RepositoryClone>> ListAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile is null)
        {
            return [];
        }

        return configFile.Clones.Select(entry => entry.ToDomain()).ToList();
    }

    public Task AddAsync(RepositoryClone record, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file =>
            {
                file.Clones.RemoveAll(entry => _SamePath(entry.Path, record.Path));
                file.Clones.Add(RepositoryCloneEntry.FromDomain(record));
            },
            cancellationToken);

    public Task RemoveAsync(string path, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Clones.RemoveAll(entry => _SamePath(entry.Path, path)),
            cancellationToken);

    private static bool _SamePath(string left, string right) =>
        string.Equals(
            System.IO.Path.GetFullPath(left),
            System.IO.Path.GetFullPath(right),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
