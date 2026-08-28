namespace Cockpit.Core.Diagnostics;

// AC-1196: a job posted to the UI thread that has not run yet is one of two different faults. A starved dispatcher
// keeps pumping and never reaches the low priority the job sits at; a blocked one runs nothing at all. Only the
// second may be blamed on the render clock, and telling them apart is what this does.
public static class UiDispatchHeartbeat
{
    // The same budget as the render clock's: a posted job nobody has picked up in this long is not a busy machine
    // either way, and a second number for one probe would only be a second number to keep in step.
    public static readonly TimeSpan StarvedAfter = RenderClockHeartbeat.StallAfter;

    // How fresh the high-priority pong has to be for the thread to count as pumping. It is posted every tick and
    // outranks what a runaway layout loop reposts at, so on a live thread it is about a second old; three gives a
    // long layout pass room without letting a thread that answers nothing read as merely starved.
    public static readonly TimeSpan PumpingWithin = TimeSpan.FromSeconds(3);

    // pendingFor: how long the posted probe has waited without running; null once it runs, or when none is out.
    // sinceHighPriorityPong: how long since work above a layout loop's priority last ran; null when none ever has,
    // which reads as blocked rather than starved — a pumping thread would have answered it.
    public static bool IsStarved(TimeSpan? pendingFor, TimeSpan? sinceHighPriorityPong, TimeSpan? starvedAfter = null) =>
        pendingFor > (starvedAfter ?? StarvedAfter) && sinceHighPriorityPong < PumpingWithin;

    // Same warn-once/calm-later shape as RenderClockHeartbeat.Decide. A null pendingFor is the healthy case and
    // also what ends the alarm: the probe was picked up, so the low priorities are being reached again.
    public static UiDispatchDecision Decide(
        TimeSpan? pendingFor, TimeSpan? sinceHighPriorityPong, bool warned, TimeSpan? starvedAfter = null)
    {
        if (!warned && IsStarved(pendingFor, sinceHighPriorityPong, starvedAfter))
        {
            return new UiDispatchDecision(Starved: true, Recovered: false, Warned: true);
        }

        if (warned && pendingFor is null)
        {
            return new UiDispatchDecision(Starved: false, Recovered: true, Warned: false);
        }

        return new UiDispatchDecision(Starved: false, Recovered: false, Warned: warned);
    }

    // How long the render clock has owed an answer, as far as it may be blamed for it. A probe the UI thread has
    // started is its business outright; one still waiting on a pumping thread is not (that is starvation), and one
    // waiting on a thread that answers nothing is — the commit can never be requested.
    public static TimeSpan? RenderClockOutstandingFor(
        TimeSpan? startedFor, TimeSpan? pendingFor, TimeSpan? sinceHighPriorityPong, TimeSpan? starvedAfter = null) =>
        startedFor ?? (IsStarved(pendingFor, sinceHighPriorityPong, starvedAfter) ? null : pendingFor);
}

public sealed record UiDispatchDecision(bool Starved, bool Recovered, bool Warned);
