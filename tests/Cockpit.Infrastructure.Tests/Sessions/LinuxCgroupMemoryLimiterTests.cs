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
            Assert.Equal("4321", File.ReadAllText(Path.Combine(group, "cgroup.procs")));
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
}
