using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.WorkingPaths;
using Cockpit.Core.WorkingPaths;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.WorkingPaths;

// Persists the New-session dialog's remembered working directories under the `workingPaths` section of
// `cockpit.json` (same file/pattern as the other settings stores). Reads-modifies-writes the whole file
// via `CockpitConfigFileAccess` so sibling sections are left untouched. When nothing was ever
// saved, `LoadAsync` returns `WorkingPathHistory.Empty`. The recent-list capping and
// de-duplication live in `WorkingPathHistory` so this store is just load / apply / save.
internal sealed class WorkingPathHistoryStore : IWorkingPathHistoryStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WorkingPathHistoryStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
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

    public Task<WorkingPathHistory> RemoveAsync(string path, CancellationToken cancellationToken = default) =>
        _MutateAsync(history => history.WithoutPath(path), cancellationToken);

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
