using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Layout;

/// <summary>
/// Persists <see cref="LayoutSettings"/> under the <c>layout</c> section of <c>cockpit.json</c> (same
/// file/pattern as <c>SessionBehaviorSettingsStore</c>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched. When no settings
/// were ever saved, <see cref="LoadAsync"/> returns the defaults.
/// </summary>
internal sealed class LayoutSettingsStore : ILayoutSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public LayoutSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal LayoutSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<LayoutSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Layout?.ToDomain() ?? new LayoutSettings();
    }

    public Task SaveAsync(LayoutSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Layout = LayoutSettingsEntry.FromDomain(settings),
            cancellationToken);
}
