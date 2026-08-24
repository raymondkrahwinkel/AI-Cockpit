using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

// Opens the first-run wizard shell (AC-509) and marks it complete once the window closes — Skip, Next, or a
// dismissal all reach the same `Closed` event. Also the Help menu's "Run setup again" route (AC-512), which
// re-walks the steps without undoing an earlier run. The startup gate is separate — see `App._ShowOnboardingWizard`.
internal sealed class FirstRunWizardService(IEnumerable<IFirstRunWizardStep> steps, IFirstRunWizardStateStore stateStore)
    : IFirstRunWizard, ISingletonService
{
    public async Task ShowAsync(CancellationToken cancellationToken = default)
    {
        var viewModel = new FirstRunWizardViewModel([.. steps], FirstRunWizardViewModel.EpicPlan);
        var window = new FirstRunWizardWindow { DataContext = viewModel };

        var completion = new TaskCompletionSource();
        window.Closed += (_, _) => completion.TrySetResult();

        // Owned by the main window (AC-543 defect #9): unlike the chat pop-out, the wizard has no reason to
        // outlive the cockpit, so Avalonia's Owner relationship closes it with its owner. Falls back to
        // unowned when there is no main window yet (headless test harness — see SessionDialogService).
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            window.Show(main);
        }
        else
        {
            window.Show();
        }

        // Registered only after Show(): closing a window before it has ever been shown throws "Cannot
        // re-show a closed window" the moment Show() runs next. Still must not leave completion.Task
        // waiting forever on a cancelled token, so this closes the window instead.
        await using var cancellation = cancellationToken.Register(window.Close);

        await completion.Task;
        cancellationToken.ThrowIfCancellationRequested();

        await stateStore.MarkCompletedAsync(FirstRunWizardVersion.Current, cancellationToken);
    }
}
