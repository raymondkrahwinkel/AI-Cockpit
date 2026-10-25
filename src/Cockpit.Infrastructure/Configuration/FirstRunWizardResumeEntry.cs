using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Configuration;

internal sealed class FirstRunWizardResumeEntry
{
    public int StartAtOrder { get; set; }

    public string StartChoiceId { get; set; } = string.Empty;

    public List<FirstRunProviderChoiceEntry> Providers { get; set; } = [];

    public string? AssistantProviderId { get; set; }

    public string? AssistantProviderName { get; set; }

    public static FirstRunWizardResumeEntry FromDomain(FirstRunWizardResumeState state) => new()
    {
        StartAtOrder = state.StartAtOrder,
        StartChoiceId = state.StartChoiceId,
        Providers = [.. state.Providers.Select(provider => new FirstRunProviderChoiceEntry { Id = provider.Id, Name = provider.Name })],
        AssistantProviderId = state.AssistantProviderId,
        AssistantProviderName = state.AssistantProviderName,
    };

    public FirstRunWizardResumeState ToDomain() => new(
        StartAtOrder,
        StartChoiceId,
        [.. Providers.Select(provider => new FirstRunProviderChoice(provider.Id, provider.Name))],
        AssistantProviderId,
        AssistantProviderName);
}

internal sealed class FirstRunProviderChoiceEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}
