using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// AC-692: proves this class only ever configures the throttle file (<c>memory.high</c>), never the kernel's hard
/// OOM-kill boundary (<c>memory.max</c>) — against a real temp directory standing in for cgroupfs, which no dev or
/// CI machine for this repo's other two platforms has.
/// </summary>
public class LinuxCgroupMemoryLimiterTests
{
    private const long Megabyte = 1024 * 1024;

    [Fact]
    public void Apply_WritesTheThrottleFile_NeverTheHardKillFile()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-cgroup-test-");
        try
        {
            var limiter = new LinuxCgroupMemoryLimiter(NullLogger.Instance, () => root.FullName);

            using var handle = limiter.Apply(4321, 512 * Megabyte);
            Assert.NotNull(handle);

            var group = Path.Combine(root.FullName, LinuxCgroupMemoryLimiter.GroupNameFor(Environment.ProcessId, 4321));
            Assert.Equal((512 * Megabyte).ToString(), File.ReadAllText(Path.Combine(group, "memory.high")));
            Assert.Equal(["4321"], _Enrolled(group));
            Assert.False(
                File.Exists(Path.Combine(group, "memory.max")),
                "AC-692: this class must never set the kernel's hard OOM-kill boundary.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // Deliberately not tested: the rmdir in `CgroupHandle.Dispose` relies on real cgroupfs's "empty enough to
    // rmdir" semantics, which a temp directory holding ordinary files does not share. The kill below is testable
    // because it is one write to one file, which is exactly why AC-1093 uses it.

    [Fact]
    public void Dispose_EndsWhatTheSessionLeftRunning()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-cgroup-test-");
        try
        {
            var limiter = new LinuxCgroupMemoryLimiter(NullLogger.Instance, () => root.FullName);
            var handle = limiter.Apply(4600, 512 * Megabyte);
            Assert.NotNull(handle);

            // The kernel puts `cgroup.kill` in a v2 group itself (5.14+); a temp directory needs it placed there.
            var group = Path.Combine(root.FullName, LinuxCgroupMemoryLimiter.GroupNameFor(Environment.ProcessId, 4600));
            var kill = Path.Combine(group, "cgroup.kill");
            File.WriteAllText(kill, string.Empty);

            handle.Dispose();

            Assert.Equal("1\n", File.ReadAllText(kill));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Dispose_WhenItCannotEndThem_SaysSoWithTheReason()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-cgroup-test-");
        try
        {
            // No `cgroup.kill` in the group is what a kernel older than 5.14 looks like. AC-1093 criterion 5: that
            // is a reported outcome with its reason, never a best-effort that reads as success.
            var log = new _CapturingLogger();
            var limiter = new LinuxCgroupMemoryLimiter(log, () => root.FullName);
            var handle = limiter.Apply(4700, 512 * Megabyte);
            Assert.NotNull(handle);

            handle.Dispose();

            Assert.Contains(log.Warnings, message => message.Contains("cgroup.kill", StringComparison.Ordinal));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void WithNoWritableParent_TheSessionRunsUncapped()
    {
        var limiter = new LinuxCgroupMemoryLimiter(NullLogger.Instance, () => null);

        Assert.Null(limiter.Apply(4323, 512 * Megabyte));
    }

    [Fact]
    public void Apply_MovesInWhatTheSessionHadAlreadyForked()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-cgroup-test-");
        try
        {
            // The shape AC-1086 measured: the CLI forks an npm supervisor which forks the real MCP server, all of
            // it seconds before Apply runs. A grandchild proves the walk recurses rather than taking one level.
            var tree = new Dictionary<int, IReadOnlyList<int>>
            {
                [4400] = [4401, 4402],
                [4401] = [4403],
            };

            var limiter = new LinuxCgroupMemoryLimiter(
                NullLogger.Instance,
                () => root.FullName,
                pid => tree.TryGetValue(pid, out var children) ? children : []);

            using var handle = limiter.Apply(4400, 512 * Megabyte);
            Assert.NotNull(handle);

            var group = Path.Combine(root.FullName, LinuxCgroupMemoryLimiter.GroupNameFor(Environment.ProcessId, 4400));
            Assert.Equal(["4400", "4401", "4402", "4403"], _Enrolled(group).Order());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Apply_StillCapsTheSessionWhenTheSweepStumbles()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-cgroup-test-");
        try
        {
            // A child that exits mid-walk takes its procfs entry with it. The group already exists and already
            // holds the session by then, so the read failing must cost the strays behind it and not the handle.
            var limiter = new LinuxCgroupMemoryLimiter(
                NullLogger.Instance,
                () => root.FullName,
                pid => pid == 4500 ? [4501, 4502] : throw new IOException("this process is gone"));

            using var handle = limiter.Apply(4500, 512 * Megabyte);
            Assert.NotNull(handle);

            var group = Path.Combine(root.FullName, LinuxCgroupMemoryLimiter.GroupNameFor(Environment.ProcessId, 4500));
            Assert.Equal(["4500", "4501", "4502"], _Enrolled(group).Order());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // The pids the limiter wrote into the group, in the order it wrote them.
    private static string[] _Enrolled(string group) =>
        File.ReadAllLines(Path.Combine(group, "cgroup.procs"));

    private sealed class _CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => _NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class _NullScope : IDisposable
        {
            public static readonly _NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
