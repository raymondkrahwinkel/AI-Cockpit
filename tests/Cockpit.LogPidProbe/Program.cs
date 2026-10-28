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

        var provider = label == "crash"
            ? new FileLoggerProvider(logPath, () => new SlowCrashSink(logPath))
            : new FileLoggerProvider(logPath);
        var logger = provider.CreateLogger("probe");
        if (label == "crash")
        {
            LifecycleLog.Use(LoggerFactory.Create(builder => builder.AddProvider(provider)));
            AppDomain.CurrentDomain.UnhandledException += (_, _) =>
            {
                LifecycleLog.Write("lifecycle from later crash handler");
                // Model runtimes that terminate directly after crash handlers without ProcessExit.
                Environment.FailFast("AC-1893 end immediately after the late crash handler");
            };
            LifecycleLog.Write("lifecycle before managed crash");
            throw new InvalidOperationException("AC-1893 managed crash probe");
        }

        // Complete startup rotation before announcing readiness; the next process overlaps an open stream.
        logger.LogInformation("startup {Label}", label);
        provider.Flush();
        File.WriteAllText($"{logPath}.constructed.{label}", "");

        if (label == "A")
        {
            while (!File.Exists($"{logPath}.constructed.B"))
            {
                Thread.Sleep(20);
            }
        }

        // AC-1216: the retry that used to live here belongs in FileLoggerProvider.Write itself — every
        // caller of a shared log line needs it, not just this probe. No workaround left to do here.
        for (var i = 0; i < LineCount; i++)
        {
            logger.LogInformation("probe {Label} line {Index}", label, i);
        }
    }

    private sealed class SlowCrashSink(string path) : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(500);
            base.Write(buffer, offset, count);
        }
    }
}
