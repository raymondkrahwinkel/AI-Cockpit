using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.TranscriptDisplay;

// Persists `TranscriptDisplaySettings` under the `transcriptDisplay` section of
// `cockpit.json` (same file/pattern as `SessionSwitchSettingsStore`). Reads-modifies-writes
// the whole file via `CockpitConfigFileAccess` so it leaves the other sections untouched.
// When no settings were ever saved, `LoadAsync` returns the defaults.
internal sealed class TranscriptDisplaySettingsStore : ITranscriptDisplaySettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public TranscriptDisplaySettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
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
