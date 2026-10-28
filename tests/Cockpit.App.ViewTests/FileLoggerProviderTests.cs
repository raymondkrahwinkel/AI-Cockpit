using System.Diagnostics;
using System.Text;
using Cockpit.App.Logging;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-741: <c>cockpit.log</c> must not grow without bound during a long, uninterrupted run.
/// </summary>
public class FileLoggerProviderTests
{
    [Fact]
    public void Log_WhenFileIsAtOrOverTheLimit_RollsToTheDotOneFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "cockpit.log");
            using var provider = new FileLoggerProvider(path);
            var logger = provider.CreateLogger("test");

            logger.LogInformation("{Payload}", new string('x', (int)FileLoggerProvider.MaxSizeBytes));

            logger.LogInformation("this write should find the file over the limit and roll it first");
            provider.Dispose();

            var rolloverPath = FileLoggerProvider.RolloverPathFor(path);
            Assert.True(File.Exists(rolloverPath));
            Assert.Contains("this write should find the file over the limit and roll it first", File.ReadAllText(path));

            var blockedPath = Path.Combine(dir.FullName, "blocked-rollover.log");
            using var blockedProvider = new FileLoggerProvider(blockedPath);
            var blockedLogger = blockedProvider.CreateLogger("blocked-rollover");
            blockedLogger.LogInformation("{Payload}", new string('x', (int)FileLoggerProvider.MaxSizeBytes));
            blockedProvider.Flush();
            using var reader = new FileStream(blockedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            blockedLogger.LogInformation("still writable when rollover is blocked");
            blockedProvider.Dispose();
            Assert.Contains("still writable when rollover is blocked", File.ReadAllText(blockedPath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Log_WithASlowSink_ReturnsImmediately_AndDrainsOnDisposeAndCrash()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            using var sink = new SlowSink();
            using var provider = new FileLoggerProvider(Path.Combine(dir.FullName, "slow.log"), () => sink);
            var logger = provider.CreateLogger("slow");
            logger.LogInformation("warmup");
            provider.Flush();

            var elapsed = Stopwatch.StartNew();
            logger.LogInformation("last queued line");
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(5), $"Log took {elapsed.Elapsed.TotalMilliseconds:F3} ms");
            provider.Dispose();
            Assert.Contains("last queued line", Encoding.UTF8.GetString(sink.ToArray()));
            Assert.Equal(2, sink.Writes);

            using var blockedSink = new GatedSink();
            using var bounded = new FileLoggerProvider(Path.Combine(dir.FullName, "bounded.log"), () => blockedSink, queueCapacity: 3);
            var boundedLogger = bounded.CreateLogger("bounded");
            boundedLogger.LogInformation("in-flight");
            Assert.True(blockedSink.Entered.Wait(TimeSpan.FromSeconds(3)));
            boundedLogger.LogWarning("old warning");
            boundedLogger.LogInformation("discard info");
            boundedLogger.LogWarning("middle warning");
            boundedLogger.LogWarning("new warning");
            boundedLogger.LogCritical("latest critical");
            var shutdown = Stopwatch.StartNew();
            bounded.Dispose();
            Assert.InRange(shutdown.Elapsed, TimeSpan.FromSeconds(1.8), TimeSpan.FromSeconds(2.8));
            blockedSink.Release.Set();
            bounded.Dispose();
            var retained = Encoding.UTF8.GetString(blockedSink.ToArray());
            Assert.Contains("2 log lines dropped (queue full; 1 at Warning or above)", retained);
            Assert.DoesNotContain("old warning", retained);
            Assert.DoesNotContain("discard info", retained);
            Assert.Contains("middle warning", retained);
            Assert.Contains("latest critical", retained);

            var crashPath = Path.Combine(dir.FullName, "crash.log");
            var probe = LogPidPositiveControlTests._LocateProbeOutput();
            Assert.NotNull(probe);
            using var crash = LogPidPositiveControlTests._StartProbe(probe, crashPath, "crash");
            Assert.True(crash.WaitForExit(10_000));
            Assert.NotEqual(0, crash.ExitCode);
            Assert.Contains("lifecycle before managed crash", File.ReadAllText(crashPath));
            Assert.Contains("lifecycle from later crash handler", File.ReadAllText(crashPath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class SlowSink : MemoryStream
    {
        public int Writes { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(500);
            Writes++;
            base.Write(buffer, offset, count);
        }
    }

    private sealed class GatedSink : MemoryStream
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            base.Write(buffer, offset, count);
        }
    }
}
