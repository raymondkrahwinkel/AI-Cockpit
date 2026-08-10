using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

internal static class TerminalSettle
{
    // The control's resize debounce is 50ms; 150ms leaves room for a dispatcher timer running late on a
    // loaded machine, then the poll waits out a grid still in motion.
    public static async Task WaitAsync(TerminalControl terminal)
    {
        var seen = terminal.Buffer.Rows;
        await Task.Delay(150);

        for (var poll = 0; poll < 12; poll++)
        {
            if (terminal.Buffer.Rows == seen)
            {
                return;
            }

            seen = terminal.Buffer.Rows;
            await Task.Delay(50);
        }
    }
}
