using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1138: a gateway hop onto the UI thread answers within a named cap, with a code an agent can act on, whether
/// the thread is blocked (T1) or starved by higher-priority work (T2) — and nothing lands late afterwards (T4).
/// </summary>
/// <remarks>
/// <b>Why these could not exist before.</b> The gateway tests that came before this ran on a free UI thread, where
/// <c>CheckAccess()</c> is already true and the marshalling branch is never taken — the branch that hangs in
/// production. Every test here calls from a pool thread on purpose, and asserts on the clock.
/// <para>
/// <b>T2 is not T1 with different words.</b> A blocked thread is one long job; a starved thread is a queue that
/// keeps draining without ever reaching <c>Default</c>, which is what a layout or render loop does. The app draws,
/// takes input, and answers no agent. <see cref="StarvedDispatcher"/> is that arrangement, and it lives in
/// Cockpit.TestSupport because AC-1196 (the freeze detector) and AC-1204 (the session event queue) need the same
/// one for their own fixes.
/// </para>
/// <para>
/// <b>T3 is what makes a green T1/T2 mean anything.</b> Without a quiet run in the same file, a helper that
/// answered <c>ui_unavailable</c> to everything would pass both.
/// </para>
/// </remarks>
[Collection("avalonia")]
public sealed class Ac1138UiThreadDeadlineTests
{
    private const string PaneId = "ac-1138-pane";

    [Fact]
    public async Task ATimeoutThatClaimsTheHop_PreventsTheCallbackFromRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); });
        await entered.Task;

        var runs = 0;
        var wait = Task.Run(() => UiThreadCall.DispatchAsync(
            () => { Interlocked.Increment(ref runs); return "ran"; }, deadline.Task, TimeSpan.Zero));
        deadline.SetResult();

        await Assert.ThrowsAsync<UiUnavailableException>(() => wait);
        release.SetResult();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task ACallbackThatClaimsTheHop_MakesTheTimeoutOutcomeUnknown_AndFinishesWorkOnce()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var wait = Task.Run(() => UiThreadCall.DispatchAsync(() =>
        {
            Interlocked.Increment(ref runs);
            started.SetResult();
            release.Task.GetAwaiter().GetResult();
            return "complete";
        }, deadline.Task, TimeSpan.Zero));

        await started.Task;

        deadline.SetResult();

        await Assert.ThrowsAsync<UiOutcomeUnknownException>(() => wait);
        release.SetResult();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(1, runs);
    }

    /// <summary>T4 — the effect, not the return: work abandoned at the cap is not applied when the thread returns.</summary>
    [Fact]
    public async Task WorkAbandonedAtTheCap_IsNotAppliedWhenTheUiThreadComesBack()
    {
        var (cockpit, handle) = Dispatcher.UIThread.Invoke(_HandleWithOneSession);
        var before = Dispatcher.UIThread.Invoke(() => cockpit.FindSession(PaneId)!.Statusline);

        using (StarvedDispatcher.Start(DispatcherPriority.Render))
        {
            await Assert.ThrowsAsync<UiUnavailableException>(() => handle.SetStatuslineAsync("late"));
        }

        // The abandoned hop is still queued. Draining below Default lets it run, so what follows is a statement
        // about what it did once it had the thread — not about it never having got there.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(before, Dispatcher.UIThread.Invoke(() => cockpit.FindSession(PaneId)!.Statusline));
    }

    // AC-1374: the UiThreadCall hop this file exercises now lives in the handle, not in the (now-thin) gateway
    // sink built above it — `SessionPanelHandle` is a real, direct consumer of it, same as `SessionLabelSink` was.
    private static (CockpitViewModel Cockpit, SessionPanelHandle Handle) _HandleWithOneSession()
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();

        var session = new SessionViewModel();
        session.AdoptPaneId(PaneId);
        cockpit.Sessions.Add(session);

        return (cockpit, new SessionPanelHandle(session, isEmbedded: false, () => null));
    }
}
