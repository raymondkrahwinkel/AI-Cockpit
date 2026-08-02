using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.SessionBehavior;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.SessionBehavior;

// Persists `SessionBehaviorSettings` under the `sessionBehavior` section of
// `cockpit.json` (same file/pattern as `TranscriptDisplaySettingsStore`). Reads-modifies-
// writes the whole file via `CockpitConfigFileAccess` so it leaves the other sections
// untouched. When no settings were ever saved, `LoadAsync` returns the defaults.
internal sealed class SessionBehaviorSettingsStore : ISessionBehaviorSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public SessionBehaviorSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
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
