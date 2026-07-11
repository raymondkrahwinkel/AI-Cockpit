using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// Persists <see cref="TerminalSettings"/> under the <c>terminal</c> section of <c>cockpit.json</c>
/// (same file/pattern as <c>LayoutSettingsStore</c>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched. When no settings
/// were ever saved, <see cref="LoadAsync"/> returns the defaults.
/// </summary>
internal sealed class TerminalSettingsStore : ITerminalSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public TerminalSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal TerminalSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<TerminalSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Terminal?.ToDomain() ?? new TerminalSettings();
    }

    public Task SaveAsync(TerminalSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Terminal = TerminalSettingsEntry.FromDomain(settings),
            cancellationToken);
}
