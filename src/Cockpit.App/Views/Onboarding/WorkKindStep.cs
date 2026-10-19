using Avalonia.Controls;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;

namespace Cockpit.App.Views.Onboarding;

// The wizard's work-kind step (AC-511): what kind of work this is for, the plugins that suggests, and one
// confirmation for the lot. Registers itself through `ISingletonService` like every other step, so the shell
// picks it up without being edited.
internal sealed class WorkKindStep : IFirstRunWizardStep, ISingletonService
{
    private readonly WorkKindStepViewModel _viewModel;
    private readonly StartChoiceStep? _startChoice;

    public WorkKindStep(
        IPluginStoreConfigStore storeConfigStore,
        IPluginStoreClient storeClient,
        IPluginProvisioningService provisioning,
        IPluginRegistrationStore registrationStore,
        StartChoiceStep startChoice)
        : this(new WorkKindStepViewModel(storeConfigStore, storeClient, provisioning, registrationStore), startChoice)
    {
    }

    // Renders a step over a view model that is already populated — the screenshot scenes, which have no store to read.
    internal WorkKindStep(WorkKindStepViewModel viewModel, StartChoiceStep? startChoice = null)
    {
        _viewModel = viewModel;
        _startChoice = startChoice;
    }

    // Leaves room below for the steps that come before it (the welcome page, AC-510's provider picker) without
    // this file having to know what they settled on.
    public int Order => 30;

    // Matches the epic's own label for this slot (AC-509's EpicPlan).
    public string Title => "What you work on";

    // Skipped when the route picked on the choice step settles it (AC-1504), struck through rather than removed.
    public bool IsSkipped => _startChoice?.ViewModel.AnswersWorkKind ?? false;

    public Control BuildContent() => new WorkKindStepView { DataContext = _viewModel };
}
