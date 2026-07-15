using Cockpit.Core.Workspaces;
using FluentAssertions;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// <see cref="WorkspaceSettings"/> — the workspace set, the active one, and the Ctrl+Shift+Left/Right step
/// (Raymond, 2026-07-15). <see cref="WorkspaceSettings.Normalized"/> is what stands between a hand-edited
/// <c>cockpit.json</c> and an empty window, so it carries its own cases.
/// </summary>
public class WorkspaceSettingsTests
{
    [Fact]
    public void Default_IsASingleSessionsWorkspace_SoAnOperatorWhoNeverTouchedWorkspacesSeesTodaysCockpit()
    {
        var settings = WorkspaceSettings.Default;

        settings.Workspaces.Should().ContainSingle().Which.Type.Should().Be(WorkspaceType.Sessions);
        settings.Active.Should().NotBeNull();
    }

    [Fact]
    public void Active_UnknownId_FallsBackToTheFirstWorkspace()
    {
        var first = Workspace.Create("A", WorkspaceType.Sessions);
        var settings = new WorkspaceSettings { Workspaces = [first], ActiveWorkspaceId = "gone" };

        settings.Active.Should().Be(first);
    }

    [Fact]
    public void Normalized_NoWorkspaces_YieldsTheDefaultRatherThanAnEmptyCockpit()
    {
        new WorkspaceSettings().Normalized().Workspaces.Should().ContainSingle();
    }

    [Fact]
    public void Normalized_DanglingActiveId_ResolvesToTheFirstWorkspace()
    {
        var first = Workspace.Create("A", WorkspaceType.Sessions);
        var settings = new WorkspaceSettings { Workspaces = [first], ActiveWorkspaceId = "gone" };

        settings.Normalized().ActiveWorkspaceId.Should().Be(first.Id);
    }

    [Fact]
    public void Normalized_ClampsAnOutOfRangeDashboardLayout_SoAZeroColumnGridCannotReachTheView()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard) with { Layout = new DashboardLayout { Columns = 0, Rows = 9999 } };
        var settings = new WorkspaceSettings { Workspaces = [dashboard], ActiveWorkspaceId = dashboard.Id };

        var layout = settings.Normalized().Workspaces[0].Layout;

        layout.Columns.Should().Be(DashboardLayout.MinColumns);
        layout.Rows.Should().Be(DashboardLayout.MaxRows);
    }

    [Fact]
    public void WithWorkspace_AppendsAndActivatesIt()
    {
        var added = Workspace.Create("Dashboard", WorkspaceType.Dashboard);

        var settings = WorkspaceSettings.Default.WithWorkspace(added);

        settings.Workspaces.Should().HaveCount(2);
        settings.ActiveWorkspaceId.Should().Be(added.Id);
    }

    [Fact]
    public void WithoutWorkspace_TheActiveOne_SelectsItsNeighbour()
    {
        var (a, b, c) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions), Workspace.Create("C", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b, c], ActiveWorkspaceId = b.Id };

        settings.WithoutWorkspace(b.Id).ActiveWorkspaceId.Should().Be(c.Id);
    }

    [Fact]
    public void WithoutWorkspace_TheLastOne_SelectsThePreviousOne()
    {
        var (a, b) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b], ActiveWorkspaceId = b.Id };

        settings.WithoutWorkspace(b.Id).ActiveWorkspaceId.Should().Be(a.Id);
    }

    [Fact]
    public void WithoutWorkspace_TheOnlyOne_IsRefused_SinceACockpitNeedsAWorkspace()
    {
        var settings = WorkspaceSettings.Default;

        settings.WithoutWorkspace(settings.Workspaces[0].Id).Should().BeSameAs(settings);
    }

    [Fact]
    public void WithoutWorkspace_AnInactiveOne_LeavesTheSelectionAlone()
    {
        var (a, b) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b], ActiveWorkspaceId = a.Id };

        settings.WithoutWorkspace(b.Id).ActiveWorkspaceId.Should().Be(a.Id);
    }

    [Fact]
    public void WithSteppedActive_Forward_WrapsPastTheLastWorkspace()
    {
        var (a, b) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b], ActiveWorkspaceId = b.Id };

        settings.WithSteppedActive(1).ActiveWorkspaceId.Should().Be(a.Id);
    }

    [Fact]
    public void WithSteppedActive_Backward_WrapsPastTheFirstWorkspace()
    {
        var (a, b) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b], ActiveWorkspaceId = a.Id };

        settings.WithSteppedActive(-1).ActiveWorkspaceId.Should().Be(b.Id);
    }

    [Fact]
    public void WithSteppedActive_ASingleWorkspace_IsANoOp()
    {
        var settings = WorkspaceSettings.Default;

        settings.WithSteppedActive(1).ActiveWorkspaceId.Should().Be(settings.ActiveWorkspaceId);
    }

    [Fact]
    public void WithActive_UnknownId_IsIgnored()
    {
        var settings = WorkspaceSettings.Default;

        settings.WithActive("gone").ActiveWorkspaceId.Should().Be(settings.ActiveWorkspaceId);
    }
}
