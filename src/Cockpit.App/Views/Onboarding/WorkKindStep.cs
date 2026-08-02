using Avalonia.Controls;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;

namespace Cockpit.App.Views.Onboarding;

/// <summary>
/// The wizard's work-kind step (AC-511): what kind of work this is for, the plugins that suggests, and one
/// confirmation for the lot. Registers itself through <c>ISingletonService</c> like every other step, so the shell
/// picks it up without being edited.
/// </summary>
internal sealed class WorkKindStep : IFirstRunWizardStep, ISingletonService
{
    private readonly WorkKindStepViewModel _viewModel;

    public WorkKindStep(
        IPluginStoreConfigStore storeConfigStore,
        IPluginStoreClient storeClient,
        IPluginProvisioningService provisioning,
        IPluginRegistrationStore registrationStore)
        : this(new WorkKindStepViewModel(storeConfigStore, storeClient, provisioning, registrationStore))
    {
    }

    /// <summary>Renders a step over a view model that is already populated — the screenshot scenes, which have no store to read.</summary>
    internal WorkKindStep(WorkKindStepViewModel viewModel) => _viewModel = viewModel;

    // Leaves room below for the steps that come before it (the welcome page, AC-510's provider picker) without
    // this file having to know what they settled on.
    public int Order => 30;

    public string Title => "What you do";

    // Nothing carries settings over into a fresh install yet — AC-540's Depot step is what would, and it is the
    // one line that flips here when it does.
    public bool IsSkipped => false;

    public Control BuildContent() => new WorkKindStepView { DataContext = _viewModel };
}
