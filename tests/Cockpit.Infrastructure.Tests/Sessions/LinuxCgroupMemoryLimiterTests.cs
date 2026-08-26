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

            var group = Path.Combine(root.FullName, "cockpit-session-4321");
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

    // Deliberately not tested: `CgroupHandle.Dispose`'s cleanup relies on real cgroupfs's "empty enough to rmdir"
    // semantics, which a plain temp directory holding ordinary files does not share — needs real Linux to verify.

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

            var group = Path.Combine(root.FullName, "cockpit-session-4400");
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

            var group = Path.Combine(root.FullName, "cockpit-session-4500");
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
}
