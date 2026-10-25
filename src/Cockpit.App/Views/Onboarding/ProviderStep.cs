using Avalonia.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;

namespace Cockpit.App.Views.Onboarding;

// AC-510[b]: wizard's provider page — what AI providers this host has, plus a way to add more.
// Order 20 leaves room below for AC-511's work-type step, same as WelcomeStep leaves room above at 0.
internal sealed class ProviderStep(
    IPluginStoreConfigStore storeConfigStore,
    IPluginStoreClient storeClient,
    IPluginProvisioningService provisioningService,
    IPluginAdministration plugins,
    ISessionDialogService dialogs,
    IFirstRunWizardStateStore wizardState,
    IAppRestartService restart,
    StartChoiceStep startChoice) : IFirstRunWizardStep, ISingletonService
{
    public int Order => 20;

    // The epic's own label for this slot (AC-509's EpicPlan) — "AI providers" would read fine alone, but the step
    // bar's four labels are written as one sentence read left to right.
    public string Title => "What you have";

    public bool IsSkipped => false;

    // The one the latest wizard was built over; the assistant step reads what was chosen here from it.
    internal ProviderStepViewModel? ViewModel { get; private set; }

    private FirstRunWizardResumeState? _resumeState;

    internal void Restore(FirstRunWizardResumeState state)
    {
        _resumeState = state;
        ViewModel?.Restore(state);
    }

    public string? NextBlockedReason => ViewModel?.NextBlockedReason;

    public Control BuildContent()
    {
        ViewModel = new ProviderStepViewModel(
            storeConfigStore,
            storeClient,
            provisioningService,
            plugins,
            dialogs,
            wizardState,
            restart,
            () => startChoice.ViewModel.Selected.Id);
        if (_resumeState is not null)
        {
            ViewModel.Restore(_resumeState);
        }

        return new ProviderStepView { DataContext = ViewModel };
    }
}
