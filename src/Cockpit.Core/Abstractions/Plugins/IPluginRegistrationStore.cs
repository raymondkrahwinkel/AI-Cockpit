using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Persists per-plugin enable + consent state (the <c>plugins</c> section of <c>cockpit.json</c>), keyed
/// by folder id. The plugin manager reads it to render the overview and writes it on enable/disable/
/// remove; the loader reads it to decide what to load at startup.
/// </summary>
public interface IPluginRegistrationStore
{
    Task<IReadOnlyDictionary<string, PluginRegistration>> LoadAllAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string folderId, PluginRegistration registration, CancellationToken cancellationToken = default);

    Task RemoveAsync(string folderId, CancellationToken cancellationToken = default);
}
