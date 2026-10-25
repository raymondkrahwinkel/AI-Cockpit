using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Configuration;

internal sealed class FirstRunWizardResumeEntry
{
    public int StartAtOrder { get; set; }

    public string StartChoiceId { get; set; } = string.Empty;

    public List<FirstRunProviderChoiceEntry> Providers { get; set; } = [];

    public string? AssistantProviderId { get; set; }

    public string? AssistantProviderName { get; set; }

    public string? AssistantConfigJson { get; set; }

    public static FirstRunWizardResumeEntry FromDomain(FirstRunWizardResumeState state) => new()
    {
        StartAtOrder = state.StartAtOrder,
        StartChoiceId = state.StartChoiceId,
        Providers = [.. state.Providers.Select(FirstRunProviderChoiceEntry.FromDomain)],
        AssistantProviderId = state.AssistantProviderId,
        AssistantProviderName = state.AssistantProviderName,
        AssistantConfigJson = state.AssistantConfigJson,
    };

    public FirstRunWizardResumeState ToDomain() => new(
        StartAtOrder,
        StartChoiceId,
        [.. Providers.Select(provider => provider.ToDomain())],
        AssistantProviderId,
        AssistantProviderName,
        AssistantConfigJson);
}

internal sealed class FirstRunProviderChoiceEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<FirstRunSessionProviderChoiceEntry> SessionProviders { get; set; } = [];

    public static FirstRunProviderChoiceEntry FromDomain(FirstRunProviderChoice provider) => new()
    {
        Id = provider.Id,
        Name = provider.Name,
        SessionProviders = [.. provider.SessionProviders.Select(sessionProvider => new FirstRunSessionProviderChoiceEntry { Id = sessionProvider.Id, Name = sessionProvider.Name })],
    };

    public FirstRunProviderChoice ToDomain() => new(
        Id,
        Name,
        [.. SessionProviders.Select(provider => new FirstRunSessionProviderChoice(provider.Id, provider.Name))]);
}

internal sealed class FirstRunSessionProviderChoiceEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}
