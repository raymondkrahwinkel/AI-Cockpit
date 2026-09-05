using Cockpit.Core.Depot;

namespace Cockpit.Core.Abstractions.Depot;

public interface IDepotMirrorSettingsStore
{
    /// <summary>
    /// The default mirrors root used when no override is set — shown in Options so the operator sees what "blank" means.
    /// </summary>
    string DefaultRoot { get; }

    Task<DepotMirrorSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DepotMirrorSettings settings, CancellationToken cancellationToken = default);
}
