using Avalonia.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.Views.Onboarding;

// The wizard's assistant step (AC-585) — see AssistantStepViewModel for the reuse. Order 25: a free slot after
// AC-510's provider step (20, hard requirement) and before AC-511's work-kind step (30).
internal sealed class AssistantStep : IFirstRunWizardStep, ISingletonService
{
    public AssistantStep(
        IAssistantSettingsStore settingsStore,
        IAssistantProfileStore profileStore,
        ISessionDialogService dialogService,
        IPluginProviderRegistry pluginProviderRegistry)
        : this(new AssistantStepViewModel(settingsStore, profileStore, dialogService, pluginProviderRegistry))
    {
    }

    // Renders a step over a view model that is already populated — the screenshot scenes, which have no store to read.
    internal AssistantStep(AssistantStepViewModel viewModel) => ViewModel = viewModel;

    internal AssistantStepViewModel ViewModel { get; }

    public int Order => 25;

    // Matches the epic's own label for this slot (AC-509's EpicPlan).
    public string Title => "Your assistant";

    public bool IsSkipped => false;

    public Control BuildContent() => new AssistantStepView { DataContext = ViewModel };
}
