using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Worktrees;

/// <summary>
/// Persists the worktree registry under the <c>worktrees</c> section of <c>cockpit.json</c>, going through
/// <see cref="CockpitConfigFileAccess"/> so each mutation is a gated read-modify-write that never clobbers a
/// sibling section — the same seam the profile and settings stores use.
/// </summary>
internal sealed class WorktreeRegistryStore : IWorktreeRegistry, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WorktreeRegistryStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the registry at an arbitrary config file path.</summary>
    internal WorktreeRegistryStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyList<WorktreeRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile is null)
        {
            return [];
        }

        return configFile.Worktrees.Select(entry => entry.ToDomain()).ToList();
    }

    public Task AddAsync(WorktreeRecord record, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file =>
            {
                file.Worktrees.RemoveAll(entry => _SamePath(entry.Path, record.Path));
                file.Worktrees.Add(WorktreeRegistryEntry.FromDomain(record));
            },
            cancellationToken);

    public Task RemoveAsync(string worktreePath, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Worktrees.RemoveAll(entry => _SamePath(entry.Path, worktreePath)),
            cancellationToken);

    private static bool _SamePath(string left, string right) =>
        string.Equals(
            System.IO.Path.GetFullPath(left),
            System.IO.Path.GetFullPath(right),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
