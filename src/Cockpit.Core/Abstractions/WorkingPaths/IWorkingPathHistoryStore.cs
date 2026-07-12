using Cockpit.Core.WorkingPaths;

namespace Cockpit.Core.Abstractions.WorkingPaths;

/// <summary>
/// Loads and persists the New-session dialog's remembered working directories (<see cref="WorkingPathHistory"/>)
/// in <c>cockpit.json</c>. When nothing was ever saved, <see cref="LoadAsync"/> returns
/// <see cref="WorkingPathHistory.Empty"/>. The <c>Record</c>/<c>SetFavorite</c> helpers apply the corresponding
/// <see cref="WorkingPathHistory"/> mutation and save, returning the new state.
/// </summary>
public interface IWorkingPathHistoryStore
{
    Task<WorkingPathHistory> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="path"/> as most-recently-used (front of the recent list, de-duplicated, capped) and saves. Returns the updated history.</summary>
    Task<WorkingPathHistory> RecordRecentAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Pins or unpins <paramref name="path"/> as a favorite and saves. Returns the updated history.</summary>
    Task<WorkingPathHistory> SetFavoriteAsync(string path, bool favorite, CancellationToken cancellationToken = default);
}
