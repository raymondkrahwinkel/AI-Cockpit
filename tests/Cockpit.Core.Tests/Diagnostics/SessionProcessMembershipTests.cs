using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// AC-1096: the case the parent walk gets wrong. Measured on this machine on 30-08-2026 — a session's build
/// launcher exited and its 325 MB child dropped straight out of the figure while it was still running.
/// </summary>
public class SessionProcessMembershipTests
{
    [Fact]
    public void Measure_WhenTheProcessThatSpawnedItIsGone_StillCountsTheChildAndCallsItAbandoned()
    {
        var membership = new SessionProcessMembership();
        ProcessRow[] atLaunch = [new(100, 1, TimeSpan.Zero, 50)];
        membership.Measure(atLaunch, 100);
        membership.Measure(atLaunch, 100);

        ProcessRow[] whileTheLauncherLived =
        [
            new(100, 1, TimeSpan.Zero, 50),
            new(200, 100, TimeSpan.Zero, 60, "zsh"),
            new(300, 200, TimeSpan.Zero, 325),
        ];

        // AC-1331: while the shell that started it lives, the child is work the session waits on.
        Assert.Equal(2, membership.Measure(whileTheLauncherLived, 100).OutstandingCount);

        ProcessRow[] afterItExited =
        [
            new(100, 1, TimeSpan.Zero, 50),
            new(300, 200, TimeSpan.Zero, 325),
        ];

        var measured = membership.Measure(afterItExited, 100);

        Assert.Equal(375, measured.Usage.WorkingSetBytes);
        Assert.Equal(2, measured.Count);
        Assert.Equal(1, measured.AbandonedCount);
        // AC-1331: left behind is the opposite of outstanding — nothing alive is waiting on it any more.
        Assert.Equal(0, measured.OutstandingCount);

        // What the meter did before: from the session's process alone, the child is simply not there.
        Assert.Equal(50, ProcessTree.Sum(afterItExited, 100).WorkingSetBytes);
    }

    // AC-1331: the tree measured on 2026-09-16 — a Codex `ctx_execute` run under its MCP server (`node → codex → bun →
    // zsh → dotnet`) reported as nothing. Root 100 is the Windows `.cmd` shim, 110 an agent at rest with only MCP servers,
    // 200 a terminal pane's shell with a `-bash` login shell beside its build. A shell alive at launch is looked past.
    [Theory]
    [InlineData(101, false, false, 2)]
    [InlineData(101, false, true, 0)]
    [InlineData(110, false, false, 0)]
    [InlineData(100, false, false, 2)]
    [InlineData(200, true, false, 2)]
    [InlineData(200, false, false, 1)]
    public void OutstandingCount_CountsWhatRunsUnderAShellStartedAfterLaunch_AndNeverTheRootItself(int root, bool rootIsShell, bool shellsFromLaunch, int expected)
    {
        var membership = new SessionProcessMembership();
        var atLaunch = _Tree(withShells: shellsFromLaunch);
        membership.Measure(atLaunch, root, rootIsShell);
        membership.Measure(atLaunch, root, rootIsShell);

        var later = membership.Measure(_Tree(withShells: true), root, rootIsShell);

        Assert.Equal(expected, later.OutstandingCount);
    }

    private static ProcessRow[] _Tree(bool withShells)
    {
        ProcessRow[] agents =
        [
            new(100, 1, TimeSpan.Zero, 0, "cmd.exe"),
            new(101, 100, TimeSpan.Zero, 0, "node"),
            new(102, 101, TimeSpan.Zero, 0, "codex"),
            new(103, 102, TimeSpan.Zero, 0, "bun"),
            new(106, 101, TimeSpan.Zero, 0, "npm exec @playwright"),
            new(107, 106, TimeSpan.Zero, 0, "node"),
            new(110, 100, TimeSpan.Zero, 0, "node"),
            new(111, 110, TimeSpan.Zero, 0, "bun"),
            new(200, 1, TimeSpan.Zero, 0, "-zsh"),
            new(201, 200, TimeSpan.Zero, 0, "dotnet"),
        ];
        ProcessRow[] shells =
        [
            new(104, 103, TimeSpan.Zero, 0, "zsh"),
            new(105, 104, TimeSpan.Zero, 0, "dotnet"),
            new(202, 200, TimeSpan.Zero, 0, "-bash"),
        ];

        return withShells ? [.. agents, .. shells] : agents;
    }
}
