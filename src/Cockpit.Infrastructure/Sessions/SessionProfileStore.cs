using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Sessions;

// Persists `SessionProfile`s under the `profiles` section of
// `cockpit.json` in the app's config directory (`%APPDATA%\Cockpit` on
// Windows, via `Environment.SpecialFolder.ApplicationData`). When no config
// file exists yet, `LoadAsync` auto-detects profiles by asking each registered TTY
// provider plugin to report the ones already configured on this machine (Fase 4), so the store carries
// no provider-specific directory knowledge of its own.
internal sealed class SessionProfileStore : ISessionProfileStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;
    private readonly IPluginTtyProviderRegistry? _ttyProviderRegistry;

    public SessionProfileStore(IPluginTtyProviderRegistry ttyProviderRegistry)
        : this(CockpitConfigPath.Default, ttyProviderRegistry)
    {
    }

    // Test seam: point the store at an arbitrary config file path, optionally without a provider registry (no auto-detect).
    internal SessionProfileStore(string configFilePath, IPluginTtyProviderRegistry? ttyProviderRegistry = null)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
        _ttyProviderRegistry = ttyProviderRegistry;
    }

    public async Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (configFile is null || configFile.Profiles.Count == 0)
        {
            return AutoDetectDefaultProfiles();
        }

        return configFile.Profiles.Select(entry => entry.ToDomain()).ToList();
    }

    public Task SaveAsync(IReadOnlyList<SessionProfile> profiles, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Profiles = profiles.Select(SessionProfileEntry.FromDomain).ToList(),
            cancellationToken);

    // Asks every registered TTY provider plugin for the profiles it self-detected on this machine and mints a
    // `SessionProfile` per report, tagged with that provider's own opaque config JSON — so a fresh
    // install adopts existing logins (Claude's config directories, and any other provider's) without the store
    // knowing where any of them live.
    private IReadOnlyList<SessionProfile> AutoDetectDefaultProfiles()
    {
        if (_ttyProviderRegistry is null)
        {
            return [];
        }

        return _ttyProviderRegistry.Registrations
            .Where(registration => registration.DetectProfiles is not null)
            .SelectMany(registration => registration.DetectProfiles!()
                .Select(detected => new SessionProfile(
                    detected.Label,
                    new PluginProviderConfig(registration.ProviderId, detected.ConfigJson))))
            .ToList();
    }
}
