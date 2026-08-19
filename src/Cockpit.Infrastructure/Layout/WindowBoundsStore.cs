using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Layout;

// Persists a window's `WindowBounds` under the `WindowBounds` section of `cockpit.json`, keyed per window
// (AC-866 — one `WindowBoundsEntry` per key instead of the single main-window entry it used to be). Returns
// null when nothing was saved for that key.
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

    public async Task<WindowBounds?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.WindowBounds?.GetValueOrDefault(key)?.ToDomain();
    }

    public Task SaveAsync(string key, WindowBounds bounds, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file =>
            {
                file.WindowBounds ??= new Dictionary<string, WindowBoundsEntry>();
                file.WindowBounds[key] = WindowBoundsEntry.FromDomain(bounds);
            },
            cancellationToken);
}
