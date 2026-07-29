using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// <see cref="WorkspaceSettings"/> — the workspace set, the active one, and the Ctrl+Shift+Left/Right step
/// (Raymond, 2026-07-15). <see cref="WorkspaceSettings.Normalized"/> is what stands between a hand-edited
/// <c>cockpit.json</c> and an empty window, so it carries its own cases.
/// </summary>
public class WorkspaceSettingsTests
{
    [Fact]
    public void Default_IsASessionsWorkspacePlusTheFixedOverview_SoAnOperatorWhoNeverTouchedWorkspacesSeesTodaysCockpit()
    {
        var settings = WorkspaceSettings.Default;

        Assert.Equal(2, System.Linq.Enumerable.Count(settings.Workspaces));
        Assert.Single(settings.Workspaces, workspace => workspace.Type == WorkspaceType.Sessions);
        Assert.Single(settings.Workspaces, workspace => workspace.Type == WorkspaceType.Projects);
        Assert.NotNull(settings.Active);
        Assert.Equal(WorkspaceType.Sessions, settings.Active!.Type);
    }

    [Fact]
    public void Active_UnknownId_FallsBackToTheFirstWorkspace()
    {
        var first = Workspace.Create("A", WorkspaceType.Sessions);
        var settings = new WorkspaceSettings { Workspaces = [first], ActiveWorkspaceId = "gone" };

        Assert.Equal(first, settings.Active);
    }

    [Fact]
    public void Normalized_NoWorkspaces_YieldsTheDefaultRatherThanAnEmptyCockpit()
    {
        Assert.Equal(2, System.Linq.Enumerable.Count(new WorkspaceSettings().Normalized().Workspaces));
    }

    [Fact]
    public void Normalized_DanglingActiveId_ResolvesToTheFirstWorkspace()
    {
        var first = Workspace.Create("A", WorkspaceType.Sessions);
        var settings = new WorkspaceSettings { Workspaces = [first], ActiveWorkspaceId = "gone" };

        Assert.Equal(first.Id, settings.Normalized().ActiveWorkspaceId);
    }

    [Fact]
    public void Normalized_ClampsAnOutOfRangeDashboardLayout_SoAZeroColumnGridCannotReachTheView()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard) with { Layout = new DashboardLayout { Columns = 0, Rows = 9999 } };
        var settings = new WorkspaceSettings { Workspaces = [dashboard], ActiveWorkspaceId = dashboard.Id };

        var layout = settings.Normalized().Workspaces[0].Layout;

        Assert.Equal(DashboardLayout.MinColumns, layout.Columns);
        Assert.Equal(DashboardLayout.MaxRows, layout.Rows);
    }

    [Fact]
    public void WithWorkspace_AppendsAndActivatesIt()
    {
        var added = Workspace.Create("Dashboard", WorkspaceType.Dashboard);

        var settings = WorkspaceSettings.Default.WithWorkspace(added);

        Assert.Equal(3, System.Linq.Enumerable.Count(settings.Workspaces));
        Assert.Equal(added.Id, settings.ActiveWorkspaceId);
    }

    [Fact]
    public void WithWorkspace_ASecondProjectsOverview_IsRefused_SinceThereIsAlwaysExactlyOne()
    {
        var settings = WorkspaceSettings.Default.WithWorkspace(Workspace.Create("Projects", WorkspaceType.Projects));

        Assert.Same(WorkspaceSettings.Default, settings);
    }

    [Fact]
    public void WithoutWorkspace_TheActiveOne_SelectsItsNeighbour()
    {
        var (a, b, c) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions), Workspace.Create("C", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b, c], ActiveWorkspaceId = b.Id };

        Assert.Equal(c.Id, settings.WithoutWorkspace(b.Id).ActiveWorkspaceId);
    }

    [Fact]
    public void WithoutWorkspace_TheLastOne_SelectsThePreviousOne()
    {
        var (a, b) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b], ActiveWorkspaceId = b.Id };

        Assert.Equal(a.Id, settings.WithoutWorkspace(b.Id).ActiveWorkspaceId);
    }

    [Fact]
    public void WithoutWorkspace_TheOnlyOne_IsRefused_SinceACockpitNeedsAWorkspace()
    {
        var only = Workspace.Create("A", WorkspaceType.Sessions);
        var settings = new WorkspaceSettings { Workspaces = [only], ActiveWorkspaceId = only.Id };

        Assert.Same(settings, settings.WithoutWorkspace(only.Id));
    }

    [Fact]
    public void WithoutWorkspace_TheProjectsOverview_IsRefused_EvenWithOtherWorkspacesPresent()
    {
        // A fixture, not one of the operator's desks: WorkspacesViewModel.CanClose already greys its ✕, but
        // WorkspaceSettings refuses the removal itself, so a caller that does not ask still cannot take it away.
        var settings = WorkspaceSettings.Default;
        var overview = settings.Workspaces.Single(workspace => workspace.Type == WorkspaceType.Projects);

        Assert.Same(settings, settings.WithoutWorkspace(overview.Id));
    }

    [Fact]
    public void WithoutWorkspace_AnInactiveOne_LeavesTheSelectionAlone()
    {
        var (a, b) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b], ActiveWorkspaceId = a.Id };

        Assert.Equal(a.Id, settings.WithoutWorkspace(b.Id).ActiveWorkspaceId);
    }

    [Fact]
    public void WithSteppedActive_Forward_WrapsPastTheLastWorkspace()
    {
        var (a, b) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b], ActiveWorkspaceId = b.Id };

        Assert.Equal(a.Id, settings.WithSteppedActive(1).ActiveWorkspaceId);
    }

    [Fact]
    public void WithSteppedActive_Backward_WrapsPastTheFirstWorkspace()
    {
        var (a, b) = (Workspace.Create("A", WorkspaceType.Sessions), Workspace.Create("B", WorkspaceType.Sessions));
        var settings = new WorkspaceSettings { Workspaces = [a, b], ActiveWorkspaceId = a.Id };

        Assert.Equal(b.Id, settings.WithSteppedActive(-1).ActiveWorkspaceId);
    }

    [Fact]
    public void WithSteppedActive_ASingleWorkspace_IsANoOp()
    {
        var only = Workspace.Create("A", WorkspaceType.Sessions);
        var settings = new WorkspaceSettings { Workspaces = [only], ActiveWorkspaceId = only.Id };

        Assert.Equal(settings.ActiveWorkspaceId, settings.WithSteppedActive(1).ActiveWorkspaceId);
    }

    [Fact]
    public void WithActive_UnknownId_IsIgnored()
    {
        var settings = WorkspaceSettings.Default;

        Assert.Equal(settings.ActiveWorkspaceId, settings.WithActive("gone").ActiveWorkspaceId);
    }

    [Fact]
    public void Normalized_DroppingADuplicateOverview_KeepsTheOperatorOnTheOneThatSurvived()
    {
        // A hand-edited config with two overviews: the operator was on the second, which is the one dropped. They
        // must land on the surviving overview, not on whichever desk happens to come first.
        var sessions = Workspace.Create("Sessions", WorkspaceType.Sessions);
        var first = Workspace.Create("Projects", WorkspaceType.Projects);
        var second = Workspace.Create("Projects", WorkspaceType.Projects);
        var settings = new WorkspaceSettings { Workspaces = [sessions, first, second], ActiveWorkspaceId = second.Id };

        var normalized = settings.Normalized();

        Assert.Equal(1, normalized.Workspaces.Count(workspace => workspace.Type == WorkspaceType.Projects));
        Assert.Equal(WorkspaceType.Projects, normalized.Active!.Type);
    }
}
