using Cockpit.Core.WorkingPaths;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of the New-session dialog's remembered working directories, under the
/// <c>workingPaths</c> section of <c>cockpit.json</c>. Kept separate from
/// <see cref="WorkingPathHistory"/> so the persisted shape can evolve independently.
/// </summary>
internal sealed class WorkingPathHistoryEntry
{
    public List<string> Recent { get; set; } = [];

    public List<string> Favorites { get; set; } = [];

    public static WorkingPathHistoryEntry FromDomain(WorkingPathHistory history) => new()
    {
        Recent = history.Recent.ToList(),
        Favorites = history.Favorites.ToList(),
    };

    public WorkingPathHistory ToDomain() => new(Recent, Favorites);
}
