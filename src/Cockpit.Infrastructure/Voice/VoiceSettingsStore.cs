using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Persists <see cref="VoiceSettings"/> under the <c>voice</c> section of <c>cockpit.json</c> (same
/// file/pattern as <c>LayoutSettingsStore</c>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched. When no settings
/// were ever saved, <see cref="LoadAsync"/> returns the defaults (voice disabled).
/// </summary>
internal sealed class VoiceSettingsStore : IVoiceSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public VoiceSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
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
