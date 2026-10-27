namespace Cockpit.Core.ModelIntelligence;

/// <summary>Reads current public model facts while retaining the last known data when a source is unavailable.</summary>
public interface IModelIntelligence
{
    /// <summary>Returns the current or last known public snapshots without surfacing source failures.</summary>
    Task<IReadOnlyList<ModelSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches again regardless of cache age; false when the source did not answer and the cache stays.</summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);
}
