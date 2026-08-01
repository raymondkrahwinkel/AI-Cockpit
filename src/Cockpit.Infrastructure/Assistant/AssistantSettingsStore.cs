using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Assistant;

/// <summary>
/// Persists <see cref="AssistantSettings"/> under the <c>assistant</c> section of <c>cockpit.json</c> — same
/// read-modify-write pattern as <see cref="Cockpit.Infrastructure.Voice.VoiceSettingsStore"/>, so saving these
/// settings never touches the assistant's own profile section (<c>assistantProfile</c>, owned by
/// <see cref="AssistantProfileStore"/>) or any other sibling. When no settings were ever saved,
/// <see cref="LoadAsync"/> returns the defaults (assistant disabled).
/// </summary>
internal sealed class AssistantSettingsStore : IAssistantSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public AssistantSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
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
