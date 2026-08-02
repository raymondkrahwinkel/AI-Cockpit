using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Layout;

// Persists the main window's `WindowBounds` under the `windowBounds` section of
// `cockpit.json` (same file/pattern as the other settings stores). Returns null when nothing was saved.
internal sealed class WindowBoundsStore : IWindowBoundsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WindowBoundsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal WindowBoundsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<WindowBounds?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.WindowBounds?.ToDomain();
    }

    public Task SaveAsync(WindowBounds bounds, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.WindowBounds = WindowBoundsEntry.FromDomain(bounds),
            cancellationToken);
}
