using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Infrastructure.Shell;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Shell;

/// <summary>
/// Against a temp directory standing in for cgroupfs (AC-1094) — same seam as
/// <c>LinuxCgroupMemoryLimiterTests</c>, since no dev machine or CI runner for this repo's other two platforms has
/// real cgroup v2. <c>TrackedCommandRunnerTests</c> covers the real thing: an actual reparented process surviving
/// this everywhere <c>Kill(entireProcessTree: true)</c> does not.
/// </summary>
public class RunCgroupTests
{
    [Fact]
    public void Create_MakesAFreshGroup_AndExposesWhereAProcessMovesItselfIn()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-run-cgroup-test-");
        try
        {
            using var group = RunCgroup.Create("abc123", NullLogger.Instance, () => root.FullName);

            Assert.True(group.IsContained);
            var expected = Path.Combine(root.FullName, $"{LinuxCgroupMemoryLimiter.GroupPrefix}{Environment.ProcessId}-abc123");
            Assert.True(Directory.Exists(expected));
            Assert.Equal(Path.Combine(expected, "cgroup.procs"), group.ProcsPath);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void WithNoWritableParent_IsNotContained_AndHasNoProcsPath()
    {
        using var group = RunCgroup.Create("abc124", NullLogger.Instance, () => null);

        Assert.False(group.IsContained);
        Assert.Null(group.ProcsPath);

        // Nothing to clean up when there was never a group — must not throw.
        group.KillAll();
    }

    [Fact]
    public void KillAll_WritesTheKillFile()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-run-cgroup-test-");
        try
        {
            using var group = RunCgroup.Create("abc125", NullLogger.Instance, () => root.FullName);
            var kill = Path.Combine(Path.GetDirectoryName(group.ProcsPath)!, "cgroup.kill");
            File.WriteAllText(kill, string.Empty);

            group.KillAll();

            Assert.Equal("1\n", File.ReadAllText(kill));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // Deliberately not tested: the rmdir in `Dispose` relies on real cgroupfs's "empty enough to rmdir" semantics —
    // a group is a directory of virtual control files, never blocked by them — which a temp directory holding
    // ordinary files does not share. Same omission as `LinuxCgroupMemoryLimiterTests`, for the same reason.
}
