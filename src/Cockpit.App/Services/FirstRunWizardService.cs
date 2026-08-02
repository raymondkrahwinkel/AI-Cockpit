using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

// Opens the first-run wizard shell (AC-509) and marks it complete once the window closes — Skip, Next on the
// last step, or the operator dismissing it some other way all reach the same `Closed` event, so there is one
// place that sets the flag rather than one per way of leaving. This is the Help menu's "Run setup again" route
// (AC-512): running it again does not undo anything an earlier run installed, it just walks the same steps once
// more against whatever state exists now.
// The startup gate does not go through here — see `App._ShowOnboardingWizard`'s own remarks for why the
// cockpit-already-running shape this method assumes does not fit a wizard that has to replace the main window
// before one exists.
internal sealed class FirstRunWizardService(IEnumerable<IFirstRunWizardStep> steps, IFirstRunWizardStateStore stateStore)
    : IFirstRunWizard, ISingletonService
{
    public async Task ShowAsync(CancellationToken cancellationToken = default)
    {
        var viewModel = new FirstRunWizardViewModel([.. steps]);
        var window = new FirstRunWizardWindow { DataContext = viewModel };

        var completion = new TaskCompletionSource();
        window.Closed += (_, _) => completion.TrySetResult();

        // Owned by the main window (AC-543's own defect #9, AssistantIndicatorCoordinator._OpenChatAsync's own
        // comment): unlike the chat pop-out, the wizard has no reason to outlive the cockpit, so Avalonia's own
        // Owner relationship — which closes an owned window with its owner — is what is wanted here rather than
        // hand-wiring Closed forwarding. Falls back to unowned when there is no main window yet to own it (no
        // IClassicDesktopStyleApplicationLifetime exists in the headless test harness — DialogModalitySplitTests
        // notes the same gap for SessionDialogService).
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            window.Show(main);
        }
        else
        {
            window.Show();
        }

        // Registered only after Show(): an already-cancelled token invokes this callback synchronously on
        // Register, and closing a window before it has ever been shown throws "Cannot re-show a closed window"
        // the moment Show() runs next. A cancelled token must still not leave completion.Task waiting forever —
        // this is what closes the window instead of that happening never.
        await using var cancellation = cancellationToken.Register(window.Close);

        await completion.Task;
        cancellationToken.ThrowIfCancellationRequested();

        await stateStore.MarkCompletedAsync(FirstRunWizardVersion.Current, cancellationToken);
    }
}
