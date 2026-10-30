namespace Cockpit.Core.Diagnostics;

/// <summary>Core-owned so storage maintenance works without plugins.</summary>
public interface IDockerStorageMaintenance
{
    string Summary { get; }
    IReadOnlyList<string> History { get; }
    bool CanCompact { get; }
    event Action? Changed;
    void Start();
    Task CleanupAsync(CancellationToken cancellationToken = default);
    Task RequestCompactionAsync(CancellationToken cancellationToken = default);
}
