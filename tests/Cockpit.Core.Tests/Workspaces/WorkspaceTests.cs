using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// <see cref="Workspace"/> and <see cref="WorkspaceTypeRules"/> — the typed-workspace invariant (Raymond's
/// refinement, 2026-07-15): a dashboard holds widgets, a sessions workspace holds AI sessions and terminals,
/// and neither holds the other's panes.
/// </summary>
public class WorkspaceTests
{
    /// <summary>The host types crossed with every pane kind — passed via <see cref="TheoryData{T1,T2,T3}"/> because <see cref="WorkspaceType"/> is a value, not an enum constant an attribute can carry.</summary>
    public static TheoryData<WorkspaceType, PaneKind, bool> AcceptsCases => new()
    {
        { WorkspaceType.Sessions, PaneKind.AiSession, true },
        { WorkspaceType.Sessions, PaneKind.Terminal, true },
        { WorkspaceType.Sessions, PaneKind.Widget, false },
        { WorkspaceType.Dashboard, PaneKind.Widget, true },
        { WorkspaceType.Dashboard, PaneKind.AiSession, false },
        { WorkspaceType.Dashboard, PaneKind.Terminal, false },
        // The projects overview (AC-162) owns its whole surface the way a plugin type does: it starts sessions,
        // it does not hold them.
        { WorkspaceType.Projects, PaneKind.AiSession, false },
        { WorkspaceType.Projects, PaneKind.Terminal, false },
        { WorkspaceType.Projects, PaneKind.Widget, false },
    };

    [Theory]
    [MemberData(nameof(AcceptsCases))]
    public void Accepts_MatchesTheTypedWorkspaceRule(WorkspaceType type, PaneKind kind, bool expected)
    {
        Assert.Equal(expected, WorkspaceTypeRules.Accepts(type, kind));
    }

    [Fact]
    public void Accepts_APluginType_HoldsNoGridPanes()
    {
        var pluginType = new WorkspaceType("autopilot.run");

        Assert.False(WorkspaceTypeRules.Accepts(pluginType, PaneKind.AiSession));
        Assert.False(WorkspaceTypeRules.Accepts(pluginType, PaneKind.Widget));
        Assert.False(WorkspaceTypeRules.Accepts(pluginType, PaneKind.Terminal));
    }

    [Theory]
    [InlineData("Sessions")]
    [InlineData("sessions")]
    [InlineData("SESSIONS")]
    public void FromId_AHostTypeName_ResolvesToTheHostTypeCaseInsensitively(string id)
    {
        var type = WorkspaceType.FromId(id);

        Assert.Equal(WorkspaceType.Sessions, type);
        Assert.True(type.IsBuiltIn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromId_ABlankId_FallsBackToSessions(string? id)
    {
        Assert.Equal(WorkspaceType.Sessions, WorkspaceType.FromId(id));
    }

    [Theory]
    [InlineData("Projects")]
    [InlineData("projects")]
    public void FromId_TheProjectsOverview_ResolvesToTheBuiltInType(string id)
    {
        var type = WorkspaceType.FromId(id);

        Assert.Equal(WorkspaceType.Projects, type);
        Assert.True(type.IsBuiltIn, "a saved projects workspace must not come back as an unknown plugin type");
    }

    [Fact]
    public void FromId_APluginId_IsKeptVerbatimAsANonBuiltInType()
    {
        var type = WorkspaceType.FromId("autopilot.run");

        Assert.Equal("autopilot.run", type.Id);
        Assert.False(type.IsBuiltIn);
    }

    [Fact]
    public void WithPane_AnAcceptedKind_AddsIt()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard);

        var updated = dashboard.WithPane(new WorkspacePane("p1", PaneKind.Widget) { WidgetId = "clock.time" });

        Assert.Equal("clock.time", Assert.Single(updated.Panes).WidgetId);
    }

    [Fact]
    public void WithPane_AKindTheTypeRejects_Throws_RatherThanPersistingAPaneNoViewCanRender()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard);

        var act = () => dashboard.WithPane(new WorkspacePane("p1", PaneKind.AiSession));

        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void WithPane_LeavesTheOriginalUntouched()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard);

        dashboard.WithPane(new WorkspacePane("p1", PaneKind.Widget));

        Assert.Empty(dashboard.Panes);
    }

    [Fact]
    public void WithoutPane_RemovesOnlyThatPane()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard)
            .WithPane(new WorkspacePane("p1", PaneKind.Widget))
            .WithPane(new WorkspacePane("p2", PaneKind.Widget));

        Assert.Equal("p2", Assert.Single(dashboard.WithoutPane("p1").Panes).Id);
    }

    [Fact]
    public void WithoutPane_AnUnknownId_IsANoOp()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard).WithPane(new WorkspacePane("p1", PaneKind.Widget));

        Assert.Single(dashboard.WithoutPane("gone").Panes);
    }

    [Fact]
    public void WithPaneMoved_RepositionsThatPaneOnly()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard)
            .WithPane(new WorkspacePane("p1", PaneKind.Widget))
            .WithPane(new WorkspacePane("p2", PaneKind.Widget) { Cell = new GridCell(1, 0) });

        var moved = dashboard.WithPaneMoved("p1", new GridCell(0, 3));

        Assert.Equal(new GridCell(0, 3), moved.Panes.Single(pane => pane.Id == "p1").Cell);
        Assert.Equal(new GridCell(1, 0), moved.Panes.Single(pane => pane.Id == "p2").Cell);
    }

    [Fact]
    public void Create_GivesEachWorkspaceItsOwnId()
    {
        Assert.NotEqual(Workspace.Create("A", WorkspaceType.Sessions).Id, Workspace.Create("A", WorkspaceType.Sessions).Id);
    }
}
