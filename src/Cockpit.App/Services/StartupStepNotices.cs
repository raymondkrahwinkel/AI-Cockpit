using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Startup;
using Cockpit.Core.Toasts;

namespace Cockpit.App.Services;

// AC-1827: a toast, never a modal; only a plugin's step offers Retry, the cockpit's own keep loading in the background.
public static class StartupStepNotices
{
    public static void Show(StartupSequence sequence, IToastService toasts)
    {
        foreach (var step in sequence.Steps.Where(step => step.State is StartupStepState.Skipped or StartupStepState.Failed))
        {
            _Show(sequence, toasts, step);
        }
    }

    private static void _Show(StartupSequence sequence, IToastService toasts, StartupStep step)
    {
        var severity = step.State == StartupStepState.Failed ? ToastSeverity.Warning : ToastSeverity.Information;
        if (!step.IsPlugin)
        {
            toasts.Show(step.Message ?? step.Label, severity);

            return;
        }

        toasts.Show(step.Message ?? step.Label, severity, "Retry", () => _ = _RetryAsync(sequence, toasts, step));
    }

    private static async Task _RetryAsync(StartupSequence sequence, IToastService toasts, StartupStep step)
    {
        await sequence.RetryAsync(step);
        if (step.State == StartupStepState.Done)
        {
            toasts.Show($"{step.Label.TrimEnd('…', '.', ' ')} is ready.", ToastSeverity.Success);

            return;
        }

        _Show(sequence, toasts, step);
    }
}
