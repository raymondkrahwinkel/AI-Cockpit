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

    // Waits for a real sizing pass, not just "unchanged since the last poll" (see PR description for why that
    // was wrong on both ends): the grid must differ from the unmeasured default at least once, then hold that
    // value for a full debounce window. A deadline with no measurement is a hard failure, not a silent pass.
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
