using Cockpit.App.ViewModels;
using Cockpit.Core.Toasts;

namespace Cockpit.Core.Tests.Toasts;

/// <summary>
/// <see cref="ToastHostViewModel"/>'s mutation logic: <see cref="ToastHostViewModel.Add"/> adds to
/// <see cref="ToastHostViewModel.Toasts"/>, dismissal (close button, action button, or auto-dismiss
/// elapsing) removes it again, and a higher severity gets a longer auto-dismiss delay. The auto-dismiss
/// scheduler is injected as a fake here — pumping a real Avalonia dispatcher timer is not practical in a
/// unit test (same reasoning as the voice coordinators' UI-thread seams), so these tests drive "the timeout
/// elapsed" by invoking the captured callback directly instead of waiting on wall-clock time.
/// </summary>
public class ToastHostViewModelTests
{
    [Fact]
    public void Add_AppendsToastToCollection()
    {
        var host = _CreateHost(out _, out _);

        var toast = host.Add("Hello", ToastSeverity.Information, null, null);

        var single = Assert.Single(host.Toasts);
        Assert.Same(toast, single);
        Assert.Equal("Hello", toast.Message);
        Assert.Equal(ToastSeverity.Information, toast.Severity);
    }

    [Fact]
    public void Add_MultipleToasts_AllRemainUntilDismissed()
    {
        var host = _CreateHost(out _, out _);

        host.Add("First", ToastSeverity.Success, null, null);
        host.Add("Second", ToastSeverity.Warning, null, null);

        Assert.Equal(2, System.Linq.Enumerable.Count(host.Toasts));
    }

    [Fact]
    public void CloseCommand_RemovesTheToastFromTheCollection()
    {
        var host = _CreateHost(out _, out _);
        var toast = host.Add("Hello", ToastSeverity.Information, null, null);

        toast.CloseCommand.Execute(null);

        Assert.Empty(host.Toasts);
    }

    [Fact]
    public void AutoDismissElapsing_RemovesTheToast()
    {
        var host = _CreateHost(out _, out var scheduledDismissCallbacks);
        host.Add("Hello", ToastSeverity.Information, null, null);

        // Simulates the auto-dismiss timeout elapsing, without waiting on real wall-clock time.
        Assert.Single(scheduledDismissCallbacks);
        scheduledDismissCallbacks[0].Invoke();

        Assert.Empty(host.Toasts);
    }

    [Fact]
    public void Add_ErrorSeverity_SchedulesALongerAutoDismissThanOtherSeverities()
    {
        var host = _CreateHost(out var recordedDelays, out _);

        host.Add("Something broke", ToastSeverity.Error, null, null);
        host.Add("All good", ToastSeverity.Success, null, null);

        Assert.Equal(2, System.Linq.Enumerable.Count(recordedDelays));
        Assert.True(recordedDelays[0] > recordedDelays[1]);
    }

    [Fact]
    public void InvokeActionCommand_RunsTheCallback_ThenDismisses()
    {
        var host = _CreateHost(out _, out _);
        var invoked = false;
        var toast = host.Add("Update available", ToastSeverity.Information, "View", () => invoked = true);

        toast.InvokeActionCommand.Execute(null);

        Assert.True(invoked);
        Assert.Empty(host.Toasts);
    }

    [Fact]
    public void Add_NoActionCallback_HasActionIsFalse()
    {
        var host = _CreateHost(out _, out _);

        var toast = host.Add("Hello", ToastSeverity.Information, null, null);

        Assert.False(toast.HasAction);
    }

    // Records every scheduled delay (call order) and, separately, an invokable callback per toast that
    // simulates that toast's timeout elapsing — without a real timer or dispatcher.
    private static ToastHostViewModel _CreateHost(out List<TimeSpan> recordedDelays, out List<Action> scheduledDismissCallbacks)
    {
        var delays = new List<TimeSpan>();
        var callbacks = new List<Action>();
        var host = new ToastHostViewModel((toast, delay) =>
        {
            delays.Add(delay);
            callbacks.Add(() => toast.CloseCommand.Execute(null));
        });
        recordedDelays = delays;
        scheduledDismissCallbacks = callbacks;
        return host;
    }
}
