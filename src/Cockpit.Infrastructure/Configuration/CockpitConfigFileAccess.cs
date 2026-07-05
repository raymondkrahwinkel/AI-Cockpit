using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Shared read-modify-write access to the single <c>cockpit.json</c> file. Both the profile store
/// and the notification store go through this so each can update its own section without clobbering
/// the other's: they always load the full <see cref="CockpitConfigFile"/>, mutate one section, and
/// write the whole file back.
/// </summary>
internal sealed class CockpitConfigFileAccess(string configFilePath)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ConfigFilePath => configFilePath;

    public async Task<CockpitConfigFile?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(configFilePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(configFilePath);
        return await JsonSerializer.DeserializeAsync<CockpitConfigFile>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the current file (or a fresh, empty one), applies <paramref name="mutate"/> to a single
    /// section, and writes the whole document back — preserving every other section.
    /// </summary>
    public async Task UpdateAsync(Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        var configFile = await ReadAsync(cancellationToken).ConfigureAwait(false) ?? new CockpitConfigFile();
        mutate(configFile);

        var directory = Path.GetDirectoryName(configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(configFilePath);
        await JsonSerializer.SerializeAsync(stream, configFile, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }
}
