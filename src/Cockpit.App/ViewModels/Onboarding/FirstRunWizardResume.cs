using Cockpit.App.Services;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.ViewModels.Onboarding;

internal static class FirstRunWizardResume
{
    public static bool NeedsLoadedProviders(FirstRunWizardResumeState? state) => false;

    public static void Apply(
        IReadOnlyList<IFirstRunWizardStep> steps,
        FirstRunWizardResumeState? state,
        DeferredAssistantProfileCoordinator? coordinator)
    {
        if (state is null)
        {
            return;
        }

        steps.OfType<StartChoiceStep>().FirstOrDefault()?.Restore(state.StartChoiceId);
        steps.OfType<ProviderStep>().FirstOrDefault()?.Restore(state);
        steps.OfType<AssistantStep>().FirstOrDefault()?.Restore(state);
        coordinator?.Remember(state);
    }
}
