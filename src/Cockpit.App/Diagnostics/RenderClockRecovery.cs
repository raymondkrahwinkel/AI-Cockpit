namespace Cockpit.App.Diagnostics;

// AC-1104: cutting a runaway layout pass throws out of MediaContext.RenderCore, skipping the compositor commit at
// its end while LayoutManager._queued stays true — so nothing asks for another render and the UI thread sits idle
// at ~0% CPU (measured: 38.1s and 168.2s on 27-08). An animation frame reaches ScheduleRender and lifts it.
internal static class RenderClockRecovery
{
    // Avalonia raises a plain InvalidOperationException for the cut-off, so the message is the only marker.
    private const string CutOffLayoutLoopMessage = "Infinite layout loop detected";

    // Recovering re-runs the pass that threw, so a loop that is still live throws again at once. The measured
    // clusters sat 1-2s apart, so this keeps a throw-recover cycle from turning into a hot one.
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    public static bool ShouldRecover(Exception exception, TimeSpan sinceLastRecovery)
        => exception is InvalidOperationException { Message: CutOffLayoutLoopMessage }
           && sinceLastRecovery >= MinimumInterval;
}
