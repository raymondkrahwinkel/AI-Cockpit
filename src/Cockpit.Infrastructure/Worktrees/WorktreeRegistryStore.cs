using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Worktrees;

// Persists the worktree registry under the `worktrees` section of `cockpit.json`, going through
// `CockpitConfigFileAccess` so each mutation is a gated read-modify-write that never clobbers a
// sibling section — the same seam the profile and settings stores use.
internal sealed class WorktreeRegistryStore : IWorktreeRegistry, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WorktreeRegistryStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the registry at an arbitrary config file path.
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

    public async Task<WorktreeRecord?> TransferAsync(
        string worktreePath,
        string expectedSessionId,
        string targetSessionId,
        CancellationToken cancellationToken = default)
    {
        WorktreeRecord? transferred = null;
        await _configFile.UpdateAsync(
            file =>
            {
                var existing = file.Worktrees.FirstOrDefault(entry => _SamePath(entry.Path, worktreePath));
                if (existing is null || !string.Equals(existing.SessionId, expectedSessionId, StringComparison.Ordinal))
                {
                    return;
                }

                transferred = existing.ToDomain() with { SessionId = targetSessionId, IsRetained = false };
                file.Worktrees.Remove(existing);
                file.Worktrees.Add(WorktreeRegistryEntry.FromDomain(transferred));
            },
            cancellationToken).ConfigureAwait(false);

        return transferred;
    }

    public Task RemoveAsync(string worktreePath, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Worktrees.RemoveAll(entry => _SamePath(entry.Path, worktreePath)),
            cancellationToken);

    private static bool _SamePath(string left, string right) =>
        string.Equals(
            System.IO.Path.GetFullPath(left),
            System.IO.Path.GetFullPath(right),
            GitPaths.PlatformComparison);
}
