using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Screenshots;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Screenshots;

// Persists `ScreenshotSettings` under the `screenshots` section of `cockpit.json` (same
// file/pattern as `VoiceSettingsStore`). Reads-modifies-writes the whole file via
// `CockpitConfigFileAccess` so it leaves the other sections untouched. When nothing was ever
// saved, `LoadAsync` returns the defaults (the global hotkey off).
internal sealed class ScreenshotSettingsStore : IScreenshotSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ScreenshotSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal ScreenshotSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<ScreenshotSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Screenshots?.ToDomain() ?? new ScreenshotSettings();
    }

    public Task SaveAsync(ScreenshotSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Screenshots = ScreenshotSettingsEntry.FromDomain(settings),
            cancellationToken);
}
