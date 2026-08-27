using Cockpit.App.Logging;
using Microsoft.Extensions.Logging;

namespace Cockpit.LogPidProbe;

// AC-1147: a real, separate process for the positive control — LogPidPositiveControlTests spawns two of
// these against the same log path and checks the pid each line carries. Args: <logPath> <label: A|B>.
internal static class Program
{
    private const int LineCount = 50;

    private static void Main(string[] args)
    {
        var logPath = args[0];
        var label = args[1];

        // FileLoggerProvider's constructor rotates the previous run's log file, and that step is not
        // safe against a second process doing the same rename at the same instant. B waits for A to finish
        // constructing before constructing itself, so the two never do it concurrently — only the write
        // path below (what this ticket actually adds a pid to) needs to survive two live writers.
        if (label == "B")
        {
            while (!File.Exists($"{logPath}.constructed.A"))
            {
                Thread.Sleep(20);
            }
        }

        var provider = new FileLoggerProvider(logPath);
        var logger = provider.CreateLogger("probe");
        File.WriteAllText($"{logPath}.constructed.{label}", "");

        if (label == "A")
        {
            while (!File.Exists($"{logPath}.constructed.B"))
            {
                Thread.Sleep(20);
            }
        }

        for (var i = 0; i < LineCount; i++)
        {
            _WriteWithRetry(logger, label, i);
        }
    }

    // Two processes appending to the same file can hit a transient sharing violation if their writes land in
    // the same instant — an existing trait of File.AppendAllText, not something this ticket changes. A real
    // multi-writer client would retry, so the probe does too.
    private static void _WriteWithRetry(ILogger logger, string label, int index)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                logger.LogInformation("probe {Label} line {Index}", label, index);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(5);
            }
        }
    }
}
