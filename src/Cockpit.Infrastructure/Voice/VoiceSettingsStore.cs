using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Voice;

// Persists `VoiceSettings` under the `voice` section of `cockpit.json` (same file/pattern as
// `LayoutSettingsStore`). Reads-modifies-writes the whole file via `CockpitConfigFileAccess` so other
// sections stay untouched; `LoadAsync` returns the defaults (voice disabled) when nothing was saved.
internal sealed class VoiceSettingsStore : IVoiceSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public VoiceSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal VoiceSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<VoiceSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Voice?.ToDomain() ?? new VoiceSettings();
    }

    public Task SaveAsync(VoiceSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Voice = VoiceSettingsEntry.FromDomain(settings),
            cancellationToken);
}
