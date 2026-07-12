using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Layout;

/// <summary>
/// Persists the main window's <see cref="WindowBounds"/> under the <c>windowBounds</c> section of
/// <c>cockpit.json</c> (same file/pattern as the other settings stores). Returns null when nothing was saved.
/// </summary>
internal sealed class WindowBoundsStore : IWindowBoundsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WindowBoundsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
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
