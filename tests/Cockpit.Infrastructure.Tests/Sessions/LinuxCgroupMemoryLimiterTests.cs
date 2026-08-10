using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// AC-692: the cgroup this class configures throttles a session over its cap (<c>memory.high</c>) — it must never
/// set the kernel's hard OOM-kill boundary (<c>memory.max</c>) again. Proved against a real temp directory standing
/// in for cgroupfs — real file I/O, not real cgroups — since no dev machine or CI runner for this repo's other two
/// platforms has cgroupfs to point at.
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

    // Deliberately not tested: `CgroupHandle.Dispose` relies on real cgroupfs treating a directory that holds only
    // its own pseudo-files (no processes, no children) as empty enough to `rmdir`. A plain temp directory holding
    // ordinary files does not share that semantic — `Directory.Delete` on it always fails as "not empty" on any
    // real filesystem, so a test built on a fake temp root would only prove something about temp directories, not
    // about the cgroupfs behaviour the code actually depends on. Needs real Linux to verify; flagged, not guessed.

    [Fact]
    public void WithNoWritableParent_TheSessionRunsUncapped()
    {
        var limiter = new LinuxCgroupMemoryLimiter(NullLogger.Instance, () => null);

        Assert.Null(limiter.Apply(4323, 512 * Megabyte));
    }
}
