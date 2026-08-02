using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Assistant;

// Persists `AssistantSettings` under the `assistant` section of `cockpit.json` — same
// read-modify-write pattern as `Cockpit.Infrastructure.Voice.VoiceSettingsStore`, so saving these
// settings never touches the assistant's own profile section (`assistantProfile`, owned by
// `AssistantProfileStore`) or any other sibling. When no settings were ever saved,
// `LoadAsync` returns the defaults (assistant disabled).
internal sealed class AssistantSettingsStore : IAssistantSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public AssistantSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal AssistantSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Assistant?.ToDomain() ?? new AssistantSettings();
    }

    public Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Assistant = AssistantSettingsEntry.FromDomain(settings),
            cancellationToken);
}
