using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Whiteboard;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Whiteboard;

// Persists the whiteboard-access master switch under the `whiteboardAccess` section of `cockpit.json` (AC-823),
// going through `CockpitConfigFileAccess` so it leaves every other section untouched. Mirrors
// DiagramAccessSettingsStore (AC-810).
internal sealed class WhiteboardAccessSettingsStore : IWhiteboardAccessSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WhiteboardAccessSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal WhiteboardAccessSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<WhiteboardAccessSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.WhiteboardAccess?.ToDomain() ?? WhiteboardAccessSettings.Default;
    }

    public Task SaveAsync(WhiteboardAccessSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.WhiteboardAccess = WhiteboardAccessSettingsEntry.FromDomain(settings),
            cancellationToken);
}
