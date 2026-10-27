namespace Cockpit.Core.ModelIntelligence;

/// <summary>Reads current public model facts while retaining the last known data when a source is unavailable.</summary>
public interface IModelIntelligence
{
    /// <summary>Returns the current or last known public snapshots without surfacing source failures.</summary>
    Task<IReadOnlyList<ModelSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default);
}
