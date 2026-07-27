using Cockpit.Infrastructure.Agents;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The coordinator's own roster (AC-391), independent of the MCP tool that drives it: enrollment is per-workspace,
/// idempotent, and two workspaces never share a partition.
/// </summary>
public sealed class WorkspaceAgentCoordinatorTests
{
    [Fact]
    public void Enroll_ThenIsEnrolled_ReportsTrueForThatPaneInThatWorkspace()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        coordinator.Enroll("workspace-a", "pane-1");

        Assert.True(coordinator.IsEnrolled("workspace-a", "pane-1"));
    }

    [Fact]
    public void IsEnrolled_ForAPaneThatNeverCalledIn_ReportsFalse()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        Assert.False(coordinator.IsEnrolled("workspace-a", "pane-1"));
    }

    [Fact]
    public void Enroll_IsIdempotent_CallingTwiceChangesNothing()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        coordinator.Enroll("workspace-a", "pane-1");
        coordinator.Enroll("workspace-a", "pane-1");

        Assert.True(coordinator.IsEnrolled("workspace-a", "pane-1"));
    }

    [Fact]
    public void Partitioning_TwoWorkspaces_DoNotShareARoster()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        coordinator.Enroll("workspace-a", "pane-1");

        // The same pane id enrolled in a different workspace is a different partition entirely — enrolling in A
        // must never make "pane-1" show up as enrolled in B, and B's own roster must still be empty of it.
        Assert.True(coordinator.IsEnrolled("workspace-a", "pane-1"));
        Assert.False(coordinator.IsEnrolled("workspace-b", "pane-1"));
    }

    [Fact]
    public void Partitioning_EnrollingInOneWorkspace_DoesNotEnrollTheSamePaneElsewhere()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        coordinator.Enroll("workspace-a", "pane-1");
        coordinator.Enroll("workspace-b", "pane-2");

        Assert.False(coordinator.IsEnrolled("workspace-a", "pane-2"));
        Assert.False(coordinator.IsEnrolled("workspace-b", "pane-1"));
    }
}
