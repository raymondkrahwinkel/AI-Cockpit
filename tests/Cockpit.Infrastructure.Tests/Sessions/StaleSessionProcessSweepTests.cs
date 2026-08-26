using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// AC-1093: proves the sweep ends what a previous run's sessions left running, leaves a live cockpit beside this
/// one alone, and reports rather than swallows the cases it cannot do either in.
/// </summary>
public class StaleSessionProcessSweepTests
{
    // The cockpit that made the group, as a pid this test controls. Which of the two numbers in the name is the
    // owner is the whole basis of the live-sibling guard, so the tests name them apart.
    private const int DeadCockpit = 99001;
    private const int LiveCockpit = 99002;

    [Fact]
    public void EndsWhatASessionOfAPreviousRunLeftRunning()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-sweep-test-");
        try
        {
            // The shape of the 26-08-2026 measurement: the session's own process is long gone and what is left in
            // the group are the MSBuild node and the VBCSCompiler that systemd adopted.
            var group = _Group(root, DeadCockpit, 317411, held: [317411, 472189]);

            var outcome = StaleSessionProcessSweep.Sweep(() => root.FullName, _ => false, ownProcessId: 4242);

            Assert.Equal(1, outcome.Groups);
            Assert.Equal(2, outcome.Processes);
            Assert.Empty(outcome.Failures);
            Assert.Null(outcome.Unavailable);
            Assert.Equal("1\n", _Killed(group));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LeavesTheLiveSessionsOfACockpitBesideThisOneAlone()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-sweep-test-");
        try
        {
            // AC-1093 criterion 4, in the one shape that can actually happen: a development build takes no
            // single-instance claim (AC-4), so a second cockpit can be running and its sessions are not ours to end.
            var live = _Group(root, LiveCockpit, 5001, held: [5001]);
            var stale = _Group(root, DeadCockpit, 5002, held: [5002]);

            var outcome = StaleSessionProcessSweep.Sweep(() => root.FullName, pid => pid == LiveCockpit, ownProcessId: 4242);

            Assert.Equal(1, outcome.Groups);
            Assert.Equal(string.Empty, _Killed(live));
            Assert.Equal("1\n", _Killed(stale));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void EndsAGroupNamingThisVeryProcess()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-sweep-test-");
        try
        {
            // The sweep runs before this cockpit has made a single group, so one naming us belongs to a dead run
            // whose pid has come round again. Skipping it would leave it uncleanable for as long as we live.
            _Group(root, ownerProcessId: 4242, sessionProcessId: 5003, held: [5003]);

            var outcome = StaleSessionProcessSweep.Sweep(() => root.FullName, _ => true, ownProcessId: 4242);

            Assert.Equal(1, outcome.Groups);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void EndsAGroupFromBeforeTheOwnerWasPartOfTheName()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-sweep-test-");
        try
        {
            // `cockpit-session-<pid>` is what a group left by a version before AC-1093 looks like. It carries no
            // owner, and a group with no owner can only be a previous run's.
            var group = Directory.CreateDirectory(Path.Combine(root.FullName, "cockpit-session-5004"));
            File.WriteAllText(Path.Combine(group.FullName, "cgroup.procs"), "5004\n");
            File.WriteAllText(Path.Combine(group.FullName, "cgroup.kill"), string.Empty);

            var outcome = StaleSessionProcessSweep.Sweep(() => root.FullName, _ => true, ownProcessId: 4242);

            Assert.Equal(1, outcome.Groups);
            Assert.Equal(1, outcome.Processes);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void WithoutCgroupKill_ReportsTheReasonAndLeavesTheGroupStanding()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-sweep-test-");
        try
        {
            // A kernel older than 5.14 has no `cgroup.kill`. AC-1093 criterion 5: that is a reported outcome with
            // its reason, and the group is not counted as cleaned.
            var group = Directory.CreateDirectory(Path.Combine(root.FullName, LinuxCgroupMemoryLimiter.GroupNameFor(DeadCockpit, 5005)));
            File.WriteAllText(Path.Combine(group.FullName, "cgroup.procs"), "5005\n");

            var outcome = StaleSessionProcessSweep.Sweep(() => root.FullName, _ => false, ownProcessId: 4242);

            Assert.Equal(0, outcome.Groups);
            Assert.Contains(outcome.Failures, failure => failure.Contains("cgroup.kill", StringComparison.Ordinal));
            Assert.True(Directory.Exists(group.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void WithNoWritableParent_SaysThereWasNothingToSweepBy()
    {
        var outcome = StaleSessionProcessSweep.Sweep(() => null, _ => false, ownProcessId: 4242);

        Assert.Equal(0, outcome.Groups);
        Assert.NotNull(outcome.Unavailable);
    }

    [Fact]
    public void SweepsNothingWhenThereIsNothingLeftOver()
    {
        var root = Directory.CreateTempSubdirectory("cockpit-sweep-test-");
        try
        {
            // A directory in the parent that is not one of ours is not ours to touch either.
            Directory.CreateDirectory(Path.Combine(root.FullName, "user.slice"));

            var outcome = StaleSessionProcessSweep.Sweep(() => root.FullName, _ => false, ownProcessId: 4242);

            Assert.Equal(0, outcome.Groups);
            Assert.Empty(outcome.Failures);
            Assert.Null(outcome.Unavailable);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // What was written to the group's `cgroup.kill`, which is the whole act of ending it. Not asserted on: that the
    // directory is gone afterwards — a temp directory holding files refuses the rmdir that a real, process-free
    // cgroup accepts, so that half needs actual cgroupfs.
    private static string _Killed(string group) =>
        File.ReadAllText(Path.Combine(group, "cgroup.kill"));

    // A session group as a previous run left it: the pids it still holds, and the `cgroup.kill` the kernel puts in
    // a real v2 group itself. Returns the group's path.
    private static string _Group(DirectoryInfo root, int ownerProcessId, int sessionProcessId, int[] held)
    {
        var group = Directory.CreateDirectory(Path.Combine(root.FullName, LinuxCgroupMemoryLimiter.GroupNameFor(ownerProcessId, sessionProcessId)));
        File.WriteAllLines(Path.Combine(group.FullName, "cgroup.procs"), held.Select(pid => pid.ToString()));
        File.WriteAllText(Path.Combine(group.FullName, "cgroup.kill"), string.Empty);

        return group.FullName;
    }
}
