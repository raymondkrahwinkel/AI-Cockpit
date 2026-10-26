namespace Cockpit.Core.Abstractions;

// The choices a first-start restart must carry across exactly once (AC-1673). This is wizard intent, not a
// half-created assistant profile: the provider does not exist in the registry until its approved plugin starts.
public sealed record FirstRunWizardResumeState(
    int StartAtOrder,
    string StartChoiceId,
    IReadOnlyList<FirstRunProviderChoice> Providers,
    string? AssistantProviderId,
    string? AssistantProviderName,
    string? AssistantConfigJson = null,
    IReadOnlyDictionary<string, string>? AssistantOptionDefaults = null);

public sealed record FirstRunProviderChoice(
    string Id,
    string Name,
    IReadOnlyList<FirstRunSessionProviderChoice>? SessionProviders = null)
{
    public IReadOnlyList<FirstRunSessionProviderChoice> SessionProviders { get; } = SessionProviders ?? [];
}

public sealed record FirstRunSessionProviderChoice(string Id, string Name);
