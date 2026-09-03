using Avalonia.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;

namespace Cockpit.App.Views.Onboarding;

// The wizard's restore step (AC-1280), in the epic's unclaimed slot 10: after the welcome, before anything is
// asked that a backup already answers. Registers itself through `ISingletonService` like every other step.
internal sealed class RestoreStep : IFirstRunWizardStep, ISingletonService
{
    public RestoreStep(IBackupService backups, IFirstRunWizardStateStore stateStore, IAppRestartService restart)
        : this(new RestoreStepViewModel(backups, stateStore, restart))
    {
    }

    // Renders a step over a view model that is already staged — the screenshot scenes, which have no archive.
    internal RestoreStep(RestoreStepViewModel viewModel) => ViewModel = viewModel;

    internal RestoreStepViewModel ViewModel { get; }

    public int Order => 10;

    public string Title => "Your backup";

    public bool IsSkipped => false;

    public Control BuildContent() => new RestoreStepView { DataContext = ViewModel };
}
