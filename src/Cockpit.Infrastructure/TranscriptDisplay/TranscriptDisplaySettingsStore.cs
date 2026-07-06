using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.TranscriptDisplay;

/// <summary>
/// Persists <see cref="TranscriptDisplaySettings"/> under the <c>transcriptDisplay</c> section of
/// <c>cockpit.json</c> (same file/pattern as <c>SessionSwitchSettingsStore</c>). Reads-modifies-writes
/// the whole file via <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched.
/// When no settings were ever saved, <see cref="LoadAsync"/> returns the defaults.
/// </summary>
internal sealed class TranscriptDisplaySettingsStore : ITranscriptDisplaySettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public TranscriptDisplaySettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal TranscriptDisplaySettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<TranscriptDisplaySettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.TranscriptDisplay?.ToDomain() ?? new TranscriptDisplaySettings();
    }

    public Task SaveAsync(TranscriptDisplaySettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.TranscriptDisplay = TranscriptDisplaySettingsEntry.FromDomain(settings),
            cancellationToken);
}
