using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

internal static class TerminalSettle
{
    // TerminalControl.Buffer starts life as this unmeasured default (see `new TerminalBuffer(80, 24)` in
    // TerminalControl.cs) — a wait that returns before the first real sizing pass mistakes the default for
    // a settled measurement (AC-923).
    private const int UnmeasuredCols = 80;
    private const int UnmeasuredRows = 24;

    private const int PollIntervalMs = 10;
    private const int DeadlineMs = 5000;

    // Waits until the grid the last layout pass asked for has reached the buffer. Polling the row count
    // cannot do it: a grid held by the deadband and one whose debounce has not fired yet read identically,
    // so any wait built on an interval is a guess that a loaded machine loses (AC-987).
    public static async Task WaitAsync(TerminalControl terminal)
    {
        var deadline = Environment.TickCount64 + DeadlineMs;

        while (Environment.TickCount64 < deadline)
        {
            if (!terminal.HasPendingResize
                && (terminal.Buffer.Cols, terminal.Buffer.Rows) != (UnmeasuredCols, UnmeasuredRows))
            {
                return;
            }

            await Task.Delay(PollIntervalMs);
        }

        throw new TimeoutException(
            $"TerminalSettle.WaitAsync timed out after {DeadlineMs}ms with the grid at " +
            $"{terminal.Buffer.Cols}x{terminal.Buffer.Rows} (pending resize: {terminal.HasPendingResize}, " +
            $"the unmeasured default is {UnmeasuredCols}x{UnmeasuredRows}).");
    }
}
