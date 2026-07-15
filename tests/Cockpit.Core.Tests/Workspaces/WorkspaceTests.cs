using Cockpit.Core.Workspaces;
using FluentAssertions;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// <see cref="Workspace"/> and <see cref="WorkspaceTypeRules"/> — the typed-workspace invariant (Raymond's
/// refinement, 2026-07-15): a dashboard holds widgets, a sessions workspace holds AI sessions and terminals,
/// and neither holds the other's panes.
/// </summary>
public class WorkspaceTests
{
    [Theory]
    [InlineData(WorkspaceType.Sessions, PaneKind.AiSession, true)]
    [InlineData(WorkspaceType.Sessions, PaneKind.Terminal, true)]
    [InlineData(WorkspaceType.Sessions, PaneKind.Widget, false)]
    [InlineData(WorkspaceType.Dashboard, PaneKind.Widget, true)]
    [InlineData(WorkspaceType.Dashboard, PaneKind.AiSession, false)]
    [InlineData(WorkspaceType.Dashboard, PaneKind.Terminal, false)]
    public void Accepts_MatchesTheTypedWorkspaceRule(WorkspaceType type, PaneKind kind, bool expected)
    {
        WorkspaceTypeRules.Accepts(type, kind).Should().Be(expected);
    }

    [Fact]
    public void WithPane_AnAcceptedKind_AddsIt()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard);

        var updated = dashboard.WithPane(new WorkspacePane("p1", PaneKind.Widget) { WidgetId = "clock.time" });

        updated.Panes.Should().ContainSingle().Which.WidgetId.Should().Be("clock.time");
    }

    [Fact]
    public void WithPane_AKindTheTypeRejects_Throws_RatherThanPersistingAPaneNoViewCanRender()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard);

        var act = () => dashboard.WithPane(new WorkspacePane("p1", PaneKind.AiSession));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WithPane_LeavesTheOriginalUntouched()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard);

        dashboard.WithPane(new WorkspacePane("p1", PaneKind.Widget));

        dashboard.Panes.Should().BeEmpty();
    }

    [Fact]
    public void WithoutPane_RemovesOnlyThatPane()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard)
            .WithPane(new WorkspacePane("p1", PaneKind.Widget))
            .WithPane(new WorkspacePane("p2", PaneKind.Widget));

        dashboard.WithoutPane("p1").Panes.Should().ContainSingle().Which.Id.Should().Be("p2");
    }

    [Fact]
    public void WithoutPane_AnUnknownId_IsANoOp()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard).WithPane(new WorkspacePane("p1", PaneKind.Widget));

        dashboard.WithoutPane("gone").Panes.Should().HaveCount(1);
    }

    [Fact]
    public void WithPaneMoved_RepositionsThatPaneOnly()
    {
        var dashboard = Workspace.Create("D", WorkspaceType.Dashboard)
            .WithPane(new WorkspacePane("p1", PaneKind.Widget))
            .WithPane(new WorkspacePane("p2", PaneKind.Widget) { Cell = new GridCell(1, 0) });

        var moved = dashboard.WithPaneMoved("p1", new GridCell(0, 3));

        moved.Panes.Single(pane => pane.Id == "p1").Cell.Should().Be(new GridCell(0, 3));
        moved.Panes.Single(pane => pane.Id == "p2").Cell.Should().Be(new GridCell(1, 0));
    }

    [Fact]
    public void Create_GivesEachWorkspaceItsOwnId()
    {
        Workspace.Create("A", WorkspaceType.Sessions).Id
            .Should().NotBe(Workspace.Create("A", WorkspaceType.Sessions).Id);
    }
}
