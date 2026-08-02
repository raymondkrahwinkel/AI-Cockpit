using Cockpit.Core.WorkingPaths;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of the New-session dialog's remembered working directories, under the
// `workingPaths` section of `cockpit.json`. Kept separate from
// `WorkingPathHistory` so the persisted shape can evolve independently.
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
