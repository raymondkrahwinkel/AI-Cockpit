using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Screenshots;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Persists <see cref="ScreenshotSettings"/> under the <c>screenshots</c> section of <c>cockpit.json</c> (same
/// file/pattern as <c>VoiceSettingsStore</c>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched. When nothing was ever
/// saved, <see cref="LoadAsync"/> returns the defaults (the global hotkey off).
/// </summary>
internal sealed class ScreenshotSettingsStore : IScreenshotSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ScreenshotSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
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
