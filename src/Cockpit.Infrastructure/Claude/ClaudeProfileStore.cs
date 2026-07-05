using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// Persists <see cref="ClaudeProfile"/>s under the <c>profiles</c> section of
/// <c>cockpit.json</c> in the app's config directory (<c>%APPDATA%\Cockpit</c> on
/// Windows, via <see cref="Environment.SpecialFolder.ApplicationData"/>). When no config
/// file exists yet, <see cref="LoadAsync"/> auto-detects profiles from the well-known
/// <c>~/.claude</c>, <c>~/.claude-personal</c>, <c>~/.claude-work</c> directories.
/// </summary>
internal sealed class ClaudeProfileStore : IClaudeProfileStore, ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _configFilePath;

    public ClaudeProfileStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cockpit", "cockpit.json"))
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal ClaudeProfileStore(string configFilePath)
    {
        _configFilePath = configFilePath;
    }

    public async Task<IReadOnlyList<ClaudeProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configFilePath))
        {
            return AutoDetectDefaultProfiles();
        }

        await using var stream = File.OpenRead(_configFilePath);
        var configFile = await JsonSerializer.DeserializeAsync<ClaudeProfileConfigFile>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (configFile is null || configFile.Profiles.Count == 0)
        {
            return AutoDetectDefaultProfiles();
        }

        return configFile.Profiles.Select(entry => entry.ToDomain()).ToList();
    }

    public async Task SaveAsync(IReadOnlyList<ClaudeProfile> profiles, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var configFile = new ClaudeProfileConfigFile
        {
            Profiles = profiles.Select(ClaudeProfileEntry.FromDomain).ToList(),
        };

        await using var stream = File.Create(_configFilePath);
        await JsonSerializer.SerializeAsync(stream, configFile, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<ClaudeProfile> AutoDetectDefaultProfiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidateConfigDirs = new[]
        {
            Path.Combine(home, ".claude"),
            Path.Combine(home, ".claude-personal"),
            Path.Combine(home, ".claude-work"),
        };

        return ClaudeProfileAutoDetector.Detect(candidateConfigDirs, Directory.Exists);
    }
}
