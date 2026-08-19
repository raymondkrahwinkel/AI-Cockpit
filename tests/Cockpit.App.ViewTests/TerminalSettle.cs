using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

internal static class TerminalSettle
{
    // TerminalControl.Buffer starts life as this unmeasured default (see `new TerminalBuffer(80, 24)` in
    // TerminalControl.cs) — a poll that lands before the first real sizing pass sees it "unchanged" and
    // mistakes the default for a settled measurement (AC-923).
    private const int UnmeasuredCols = 80;
    private const int UnmeasuredRows = 24;

    private const int PollIntervalMs = 10;
    private const int DeadlineMs = 5000;

    // The control's resize debounce is 50ms; a value that has held for less than this could still be the
    // pre-resize reading with the debounce timer just not fired yet, not a genuine settle.
    private const int MinStableMs = 150;

    // Waits for a real sizing pass, not just "unchanged since the last poll": the buffer holds the unmeasured
    // default until the control's first layout-driven resize lands, so a poll that never sees the grid move
    // off (80,24) has proven nothing — and a terminal being resized a second time starts this wait already
    // showing its old, still-valid grid, so "unchanged since the last poll" alone would return before the
    // debounced resize even fires. Polls until the grid has differed from the default at least once and has
    // then held the same value for a full debounce window; a deadline with no measurement is a hard failure.
    public static async Task WaitAsync(TerminalControl terminal)
    {
        var deadline = Environment.TickCount64 + DeadlineMs;
        var everMeasured = false;
        var seen = (terminal.Buffer.Cols, terminal.Buffer.Rows);
        var stableSince = Environment.TickCount64;

        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(PollIntervalMs);
            var current = (terminal.Buffer.Cols, terminal.Buffer.Rows);

            if (current != (UnmeasuredCols, UnmeasuredRows))
            {
                everMeasured = true;
            }

            if (current != seen)
            {
                seen = current;
                stableSince = Environment.TickCount64;
                continue;
            }

            if (everMeasured && Environment.TickCount64 - stableSince >= MinStableMs)
            {
                return;
            }
        }

        throw new TimeoutException(
            $"TerminalSettle.WaitAsync timed out after {DeadlineMs}ms without a measured grid " +
            $"(still {seen.Cols}x{seen.Rows}, the unmeasured default is {UnmeasuredCols}x{UnmeasuredRows}).");
    }
}
