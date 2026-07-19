using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// Persists the terminal-access master switch under the <c>terminalAccess</c> section of <c>cockpit.json</c> (AC-34),
/// going through <see cref="CockpitConfigFileAccess"/> so it leaves every other section untouched — the same pattern
/// as the worktree/layout settings stores.
/// </summary>
internal sealed class TerminalAccessSettingsStore : ITerminalAccessSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public TerminalAccessSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal TerminalAccessSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<TerminalAccessSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.TerminalAccess?.ToDomain() ?? TerminalAccessSettings.Default;
    }

    public Task SaveAsync(TerminalAccessSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.TerminalAccess = TerminalAccessSettingsEntry.FromDomain(settings),
            cancellationToken);
}
