namespace Cockpit.Core.Abstractions.Remote;

/// <summary>
/// A server's Docker storage setting over its admin API (AC-2035): whether the server prunes Docker's build cache and
/// old images by itself. Off keeps its disk monitoring and warnings.
/// </summary>
public interface IServerStorageSettings
{
    Task<bool> GetAutomaticDockerCleanupAsync(CancellationToken cancellationToken = default);

    Task SetAutomaticDockerCleanupAsync(bool enabled, CancellationToken cancellationToken = default);
}
