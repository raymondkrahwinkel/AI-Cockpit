using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.App.Services;

// Carries the approved first-run choice in memory while completion removes the temporary disk state. The profile
// is minted only after plugin initialization has registered the provider, never by the wizard ahead of that gate.
internal sealed class DeferredAssistantProfileCoordinator(
    IPluginRegistrations plugins,
    IAssistantProfileStore profiles,
    IAssistantSettingsStore settings) : ISingletonService
{
    private readonly object _gate = new();
    private FirstRunWizardResumeState? _pending;

    public string? PendingAssistantName
    {
        get
        {
            lock (_gate)
            {
                return _pending?.AssistantProviderName;
            }
        }
    }

    public void Remember(FirstRunWizardResumeState? state)
    {
        if (state?.AssistantProviderId is not { Length: > 0 })
        {
            return;
        }

        lock (_gate)
        {
            _pending = state;
        }
    }

    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        FirstRunWizardResumeState? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        if (pending?.AssistantProviderId is not { Length: > 0 } providerId)
        {
            return true;
        }

        if (plugins.SessionProvider(providerId) is null)
        {
            return false;
        }

        var label = string.IsNullOrWhiteSpace(pending.AssistantProviderName) ? "Assistant" : pending.AssistantProviderName;
        var configJson = string.IsNullOrWhiteSpace(pending.AssistantConfigJson) ? "{}" : pending.AssistantConfigJson;
        var profile = new SessionProfile(label, new PluginProviderConfig(providerId, configJson))
        {
            DefaultKind = ProfileSessionKind.Sdk,
            Defaults = pending.AssistantOptionDefaults is { Count: > 0 } optionDefaults
                ? new ProfileDefaults(string.Empty, string.Empty, string.Empty) { OptionDefaults = optionDefaults }
                : null,
        };
        await profiles.RepointAsync(profile, replacesStandingInstruction: false, cancellationToken).ConfigureAwait(false);
        await settings.UpdateAsync(current => current with { IsEnabled = true }, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
            }
        }

        return true;
    }
}
