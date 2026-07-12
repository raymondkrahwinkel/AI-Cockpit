using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.WorkingPaths;
using Cockpit.Core.WorkingPaths;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.WorkingPaths;

/// <summary>
/// Persists the New-session dialog's remembered working directories under the <c>workingPaths</c> section of
/// <c>cockpit.json</c> (same file/pattern as the other settings stores). Reads-modifies-writes the whole file
/// via <see cref="CockpitConfigFileAccess"/> so sibling sections are left untouched. When nothing was ever
/// saved, <see cref="LoadAsync"/> returns <see cref="WorkingPathHistory.Empty"/>. The recent-list capping and
/// de-duplication live in <see cref="WorkingPathHistory"/> so this store is just load / apply / save.
/// </summary>
internal sealed class WorkingPathHistoryStore : IWorkingPathHistoryStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WorkingPathHistoryStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal WorkingPathHistoryStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<WorkingPathHistory> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.WorkingPaths?.ToDomain() ?? WorkingPathHistory.Empty;
    }

    public Task<WorkingPathHistory> RecordRecentAsync(string path, CancellationToken cancellationToken = default) =>
        _MutateAsync(history => history.WithRecent(path), cancellationToken);

    public Task<WorkingPathHistory> SetFavoriteAsync(string path, bool favorite, CancellationToken cancellationToken = default) =>
        _MutateAsync(history => history.WithFavorite(path, favorite), cancellationToken);

    private async Task<WorkingPathHistory> _MutateAsync(Func<WorkingPathHistory, WorkingPathHistory> mutate, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var updated = mutate(current);
        await _configFile.UpdateAsync(
            file => file.WorkingPaths = WorkingPathHistoryEntry.FromDomain(updated),
            cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
