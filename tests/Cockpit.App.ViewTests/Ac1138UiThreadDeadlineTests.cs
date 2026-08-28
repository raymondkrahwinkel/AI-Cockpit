using System.Diagnostics;
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

    [Fact]
    public void AClaimAllowsOnlyOneCallback_AndNeverAfterTheTimeout()
    {
        var callbackFirst = new UiThreadCallClaim();
        var runs = 0;
        if (callbackFirst.TryClaimCallback())
        {
            Interlocked.Increment(ref runs);
        }

        if (callbackFirst.TryClaimCallback())
        {
            Interlocked.Increment(ref runs);
        }

        Assert.Equal(1, runs);
        Assert.False(callbackFirst.TryClaimTimeout());

        var timeoutFirst = new UiThreadCallClaim();
        Assert.True(timeoutFirst.TryClaimTimeout());
        Assert.False(timeoutFirst.TryClaimCallback());
    }

    /// <summary>T1 — the UI thread held by one non-yielding job: capped, not waited out.</summary>
    [Fact]
    public async Task AGatewayCalledWhileTheUiThreadIsBlocked_AnswersUiUnavailable_RatherThanWaitingItOut()
    {
        var (_, sink) = Dispatcher.UIThread.Invoke(_SinkWithOneSession);

        // Longer than the cap by a margin, which is the point: how long this job runs no longer decides how long
        // the caller waits. Before this ticket the call came back at 8 s, with no error and no sign anything was up.
        Dispatcher.UIThread.Post(() => Thread.Sleep(TimeSpan.FromSeconds(8)));

        var (failure, elapsed) = await _TimedRefusal(sink);

        Assert.Equal(UiThreadCall.DefaultGrace, failure.Deadline);
        Assert.Contains(UiUnavailableException.Code, failure.Message, StringComparison.Ordinal);
        Assert.True(elapsed < UiThreadCall.DefaultGrace + _Slack, $"answered after {elapsed}, cap is {UiThreadCall.DefaultGrace}");
    }

    /// <summary>T2 — starved at Render (4), the priority a runaway layout or render pass reposts at.</summary>
    [Fact]
    public Task AGatewayCalledWhileTheUiThreadIsStarvedAtRender_AnswersUiUnavailableWithinTheCap() =>
        _StarvedGatewayIsCapped(DispatcherPriority.Render);

    /// <summary>T2 — the same at Loaded (1), one step above the Default the gateways hop at.</summary>
    [Fact]
    public Task AGatewayCalledWhileTheUiThreadIsStarvedAtLoaded_AnswersUiUnavailableWithinTheCap() =>
        _StarvedGatewayIsCapped(DispatcherPriority.Loaded);

    /// <summary>T3 — the silent positive control: a quiet thread still answers normally, exactly once, at once.</summary>
    [Fact]
    public async Task TheSameHopOnAQuietUiThread_RunsOnceAndAnswersNormally()
    {
        var (cockpit, sink) = Dispatcher.UIThread.Invoke(_SinkWithOneSession);

        var clock = Stopwatch.StartNew();
        var applied = await Task.Run(() => sink.SetStatuslineAsync(PaneId, "AC-1138"));
        clock.Stop();

        Assert.True(applied);
        Assert.Equal("AC-1138", Dispatcher.UIThread.Invoke(() => cockpit.FindSession(PaneId)!.Statusline));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"a free UI thread took {clock.Elapsed}");

        // Counted rather than assumed: "it answered" would also be true of a helper that ran the work twice, or of
        // one that answered from somewhere other than the work.
        var runs = 0;
        var answer = await Task.Run(() => UiThreadCall.RunAsync(() => { runs++; return "answered"; }));
        Assert.Equal("answered", answer);
        Assert.Equal(1, runs);
    }

    /// <summary>T4 — the effect, not the return: work abandoned at the cap is not applied when the thread returns.</summary>
    [Fact]
    public async Task WorkAbandonedAtTheCap_IsNotAppliedWhenTheUiThreadComesBack()
    {
        var (cockpit, sink) = Dispatcher.UIThread.Invoke(_SinkWithOneSession);
        var before = Dispatcher.UIThread.Invoke(() => cockpit.FindSession(PaneId)!.Statusline);

        using (StarvedDispatcher.Start(DispatcherPriority.Render))
        {
            await Assert.ThrowsAsync<UiUnavailableException>(() => sink.SetStatuslineAsync(PaneId, "late"));
        }

        // The abandoned hop is still queued. Draining below Default lets it run, so what follows is a statement
        // about what it did once it had the thread — not about it never having got there.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(before, Dispatcher.UIThread.Invoke(() => cockpit.FindSession(PaneId)!.Statusline));
    }

    // Wall-clock room for a loaded CI box between the cap and the assertion: enough that a slow machine does not
    // fail this, far short of the 31.8 s the field measured (AC-1138 §2b).
    private static readonly TimeSpan _Slack = TimeSpan.FromSeconds(4);

    private static async Task _StarvedGatewayIsCapped(DispatcherPriority priority)
    {
        var (cockpit, sink) = Dispatcher.UIThread.Invoke(_SinkWithOneSession);

        using var starver = StarvedDispatcher.Start(priority);

        // The shape every gateway had before this ticket, queued alongside the capped one: same thread, same
        // priority, no cap. It is the control for "without the fix it waits", and it must still be waiting below.
        var uncapped = Dispatcher.UIThread.InvokeAsync(() => cockpit.SetSessionStatusline(PaneId, "uncapped")).GetTask();

        var (failure, elapsed) = await _TimedRefusal(sink);

        Assert.Equal(UiThreadCall.DefaultGrace, failure.Deadline);
        Assert.Contains(UiUnavailableException.Code, failure.Message, StringComparison.Ordinal);
        Assert.True(elapsed < UiThreadCall.DefaultGrace + _Slack, $"answered after {elapsed}, cap is {UiThreadCall.DefaultGrace}");

        Assert.False(uncapped.IsCompleted, "the uncapped hop came back, so this run proved nothing about starvation");
        Assert.True(starver.Rounds > 10, $"the thread has to have kept working, not blocked; rounds={starver.Rounds}");
    }

    private static async Task<(UiUnavailableException Failure, TimeSpan Elapsed)> _TimedRefusal(SessionLabelSink sink)
    {
        var clock = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<UiUnavailableException>(
            () => Task.Run(() => sink.SetStatuslineAsync(PaneId, "AC-1138")));
        clock.Stop();

        return (failure, clock.Elapsed);
    }

    private static (CockpitViewModel Cockpit, SessionLabelSink Sink) _SinkWithOneSession()
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();

        var session = new SessionViewModel();
        session.AdoptPaneId(PaneId);
        cockpit.Sessions.Add(session);

        return (cockpit, new SessionLabelSink(cockpit));
    }
}
