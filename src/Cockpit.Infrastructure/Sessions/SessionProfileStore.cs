using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Persists <see cref="SessionProfile"/>s under the <c>profiles</c> section of
/// <c>cockpit.json</c> in the app's config directory (<c>%APPDATA%\Cockpit</c> on
/// Windows, via <see cref="Environment.SpecialFolder.ApplicationData"/>). When no config
/// file exists yet, <see cref="LoadAsync"/> auto-detects profiles from the well-known
/// <c>~/.claude</c>, <c>~/.claude-personal</c>, <c>~/.claude-work</c> directories.
/// </summary>
internal sealed class SessionProfileStore : ISessionProfileStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public SessionProfileStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal SessionProfileStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
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

    private static IReadOnlyList<SessionProfile> AutoDetectDefaultProfiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidateConfigDirs = new[]
        {
            Path.Combine(home, ".claude"),
            Path.Combine(home, ".claude-personal"),
            Path.Combine(home, ".claude-work"),
        };

        return ClaudeCliProfileDetector.Detect(candidateConfigDirs, Directory.Exists);
    }
}
