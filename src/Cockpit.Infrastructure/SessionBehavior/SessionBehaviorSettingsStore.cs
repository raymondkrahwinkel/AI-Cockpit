using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.SessionBehavior;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.SessionBehavior;

/// <summary>
/// Persists <see cref="SessionBehaviorSettings"/> under the <c>sessionBehavior</c> section of
/// <c>cockpit.json</c> (same file/pattern as <c>TranscriptDisplaySettingsStore</c>). Reads-modifies-
/// writes the whole file via <see cref="CockpitConfigFileAccess"/> so it leaves the other sections
/// untouched. When no settings were ever saved, <see cref="LoadAsync"/> returns the defaults.
/// </summary>
internal sealed class SessionBehaviorSettingsStore : ISessionBehaviorSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public SessionBehaviorSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal SessionBehaviorSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<SessionBehaviorSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.SessionBehavior?.ToDomain() ?? new SessionBehaviorSettings();
    }

    public Task SaveAsync(SessionBehaviorSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.SessionBehavior = SessionBehaviorSettingsEntry.FromDomain(settings),
            cancellationToken);
}
