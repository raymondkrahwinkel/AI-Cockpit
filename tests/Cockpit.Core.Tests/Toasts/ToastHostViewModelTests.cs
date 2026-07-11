using Cockpit.App.ViewModels;
using Cockpit.Core.Toasts;
using FluentAssertions;

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

        host.Toasts.Should().ContainSingle().Which.Should().BeSameAs(toast);
        toast.Message.Should().Be("Hello");
        toast.Severity.Should().Be(ToastSeverity.Information);
    }

    [Fact]
    public void Add_MultipleToasts_AllRemainUntilDismissed()
    {
        var host = _CreateHost(out _, out _);

        host.Add("First", ToastSeverity.Success, null, null);
        host.Add("Second", ToastSeverity.Warning, null, null);

        host.Toasts.Should().HaveCount(2);
    }

    [Fact]
    public void CloseCommand_RemovesTheToastFromTheCollection()
    {
        var host = _CreateHost(out _, out _);
        var toast = host.Add("Hello", ToastSeverity.Information, null, null);

        toast.CloseCommand.Execute(null);

        host.Toasts.Should().BeEmpty();
    }

    [Fact]
    public void AutoDismissElapsing_RemovesTheToast()
    {
        var host = _CreateHost(out _, out var scheduledDismissCallbacks);
        host.Add("Hello", ToastSeverity.Information, null, null);

        // Simulates the auto-dismiss timeout elapsing, without waiting on real wall-clock time.
        scheduledDismissCallbacks.Should().ContainSingle();
        scheduledDismissCallbacks[0].Invoke();

        host.Toasts.Should().BeEmpty();
    }

    [Fact]
    public void Add_ErrorSeverity_SchedulesALongerAutoDismissThanOtherSeverities()
    {
        var host = _CreateHost(out var recordedDelays, out _);

        host.Add("Something broke", ToastSeverity.Error, null, null);
        host.Add("All good", ToastSeverity.Success, null, null);

        recordedDelays.Should().HaveCount(2);
        recordedDelays[0].Should().BeGreaterThan(recordedDelays[1]);
    }

    [Fact]
    public void InvokeActionCommand_RunsTheCallback_ThenDismisses()
    {
        var host = _CreateHost(out _, out _);
        var invoked = false;
        var toast = host.Add("Update available", ToastSeverity.Information, "View", () => invoked = true);

        toast.InvokeActionCommand.Execute(null);

        invoked.Should().BeTrue();
        host.Toasts.Should().BeEmpty();
    }

    [Fact]
    public void Add_NoActionCallback_HasActionIsFalse()
    {
        var host = _CreateHost(out _, out _);

        var toast = host.Add("Hello", ToastSeverity.Information, null, null);

        toast.HasAction.Should().BeFalse();
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
