using Avalonia.Controls;
using Material.Icons;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;

namespace Cockpit.App.Views.Onboarding;

// The wizard's "How do you want to start?" step (AC-1504), in the epic's slot 10. The restore page of AC-1280 is the
// content of its "Restore a backup file" choice, so the restore logic is reused here rather than copied. Routes
// that plugins contribute come in through the registry; the step only reads them.
internal sealed class StartChoiceStep : IFirstRunWizardStep, ISingletonService
{
    private readonly IFirstStartRouteRegistry? _routes;

    public StartChoiceStep(IBackupService backups, IFirstRunWizardStateStore stateStore, IAppRestartService restart, IFirstStartRouteRegistry routes)
        : this(new RestoreStepViewModel(backups, stateStore, restart), routes)
    {
    }

    // Renders a step over a restore view model that is already staged — the screenshot scenes, which have no archive.
    internal StartChoiceStep(RestoreStepViewModel restoreViewModel, IFirstStartRouteRegistry? routes = null)
    {
        _routes = routes;
        RestoreViewModel = restoreViewModel;
        ViewModel = _Create();
    }

    private StartChoiceStepViewModel _Create() =>
        new(
            [
                new StartChoiceOptionViewModel(
                    "start-fresh", MaterialIconKind.Creation, "Start fresh",
                    "Set this machine up step by step: provider, assistant, what you work on.", createContent: null),
                new StartChoiceOptionViewModel(
                    "restore-backup", MaterialIconKind.Download, "Restore a backup file",
                    "Coming from another machine? Bring the backup you made there.",
                    () => new RestoreStepView { DataContext = RestoreViewModel }),
            ],
            _routes);

    internal RestoreStepViewModel RestoreViewModel { get; }

    // The one the latest wizard was built over: each wizard gets its own, so a run opened again starts on "Start
    // fresh" and never shares a control (or a stale notice) with the one before it.
    internal StartChoiceStepViewModel ViewModel { get; private set; }

    // Why the next wizard opens here, e.g. a --provision that failed (AC-1505); read once, by the wizard being built.
    internal string Notice { get; set; } = string.Empty;

    public int Order => 10;

    // Matches the mockup's label for this slot (AC-1504; EpicPlan's own).
    public string Title => "Getting started";

    public bool IsSkipped => false;

    public string? NextBlockedReason => ViewModel.NextBlockedReason;

    public Control BuildContent()
    {
        ViewModel.Dispose();
        ViewModel = _Create();
        ViewModel.Notice = Notice;
        Notice = string.Empty;

        return new StartChoiceStepView { DataContext = ViewModel };
    }
}
