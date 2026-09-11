using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.QuickNotes;
using Cockpit.Core.QuickNotes;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.QuickNotes;

// Persists `QuickNoteSettings` under the `quickNotes` section of `cockpit.json`, read-modify-write via
// `CockpitConfigFileAccess` so other sections stay untouched.
internal sealed class QuickNoteSettingsStore : IQuickNoteSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public QuickNoteSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal QuickNoteSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<QuickNoteSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.QuickNotes?.ToDomain() ?? new QuickNoteSettings();
    }

    public Task SaveAsync(QuickNoteSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.QuickNotes = QuickNoteSettingsEntry.FromDomain(settings),
            cancellationToken);
}
