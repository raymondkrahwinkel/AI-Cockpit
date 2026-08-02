using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Rendering;
using Cockpit.Core.Rendering;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Rendering;

// Persists `RenderingSettings` under the `rendering` section of `cockpit.json` (AC-67),
// the same read-modify-write-the-whole-file pattern as the other section stores so it never clobbers a sibling.
// When nothing was ever saved, `LoadAsync` returns the defaults (Auto).
internal sealed class RenderingSettingsStore : IRenderingSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public RenderingSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal RenderingSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<RenderingSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Rendering?.ToDomain() ?? new RenderingSettings();
    }

    public Task SaveAsync(RenderingSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Rendering = RenderingSettingsEntry.FromDomain(settings),
            cancellationToken);
}
