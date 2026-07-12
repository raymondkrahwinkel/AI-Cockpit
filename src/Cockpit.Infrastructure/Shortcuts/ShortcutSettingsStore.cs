using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shortcuts;
using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Shortcuts;

/// <summary>
/// Persists the app-action shortcuts under the <c>shortcuts</c> section of <c>cockpit.json</c> (same
/// file/pattern as the other settings stores), reading-modifying-writing the whole file so sibling sections
/// stay intact. When nothing was ever saved, <see cref="LoadAsync"/> returns
/// <see cref="ShortcutSettings.Default"/>.
/// </summary>
internal sealed class ShortcutSettingsStore : IShortcutSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ShortcutSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal ShortcutSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<ShortcutSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Shortcuts?.ToDomain() ?? ShortcutSettings.Default;
    }

    public Task SaveAsync(ShortcutSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Shortcuts = ShortcutSettingsEntry.FromDomain(settings),
            cancellationToken);
}
