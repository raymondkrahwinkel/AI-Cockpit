using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Persists <see cref="SessionProfile"/>s under the <c>profiles</c> section of
/// <c>cockpit.json</c> in the app's config directory (<c>%APPDATA%\Cockpit</c> on
/// Windows, via <see cref="Environment.SpecialFolder.ApplicationData"/>). When no config
/// file exists yet, <see cref="LoadAsync"/> auto-detects profiles by asking each registered TTY
/// provider plugin to report the ones already configured on this machine (Fase 4), so the store carries
/// no provider-specific directory knowledge of its own.
/// </summary>
internal sealed class SessionProfileStore : ISessionProfileStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;
    private readonly IPluginTtyProviderRegistry? _ttyProviderRegistry;

    public SessionProfileStore(IPluginTtyProviderRegistry ttyProviderRegistry)
        : this(CockpitConfigPath.Default, ttyProviderRegistry)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path, optionally without a provider registry (no auto-detect).</summary>
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

    /// <summary>
    /// Asks every registered TTY provider plugin for the profiles it self-detected on this machine and mints a
    /// <see cref="SessionProfile"/> per report, tagged with that provider's own opaque config JSON — so a fresh
    /// install adopts existing logins (Claude's config directories, and any other provider's) without the store
    /// knowing where any of them live.
    /// </summary>
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
