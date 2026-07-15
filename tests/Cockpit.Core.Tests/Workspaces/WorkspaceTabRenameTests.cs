using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;
using FluentAssertions;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// Renaming a workspace tab in place (Raymond, 2026-07-15: "je moet de workspaces kunnen renamen uiteraard").
/// The tab owns the edit state the way a session row does; committing reports the new name and the caller
/// persists it, since the tab is a view over a stored record and does not write.
/// </summary>
public class WorkspaceTabRenameTests
{
    [Fact]
    public void BeginRename_SwapsTheLabelForAnEditBox_SeededWithTheCurrentName()
    {
        var tab = _Tab("Work");

        tab.BeginRename();

        tab.IsRenaming.Should().BeTrue();
        tab.EditName.Should().Be("Work");
    }

    [Fact]
    public void CommitRename_ReportsTheNewName_AndShowsItImmediately()
    {
        var tab = _Tab("Work");
        tab.BeginRename();
        tab.EditName = "Client work";

        var committed = tab.CommitRename();

        committed.Should().Be("Client work");
        tab.Name.Should().Be("Client work", "the strip updates before the rebuilt tabs arrive from the store");
        tab.IsRenaming.Should().BeFalse();
    }

    [Fact]
    public void CommitRename_TrimsWhitespace()
    {
        var tab = _Tab("Work");
        tab.BeginRename();
        tab.EditName = "  Client work  ";

        tab.CommitRename().Should().Be("Client work");
    }

    [Fact]
    public void CommitRename_Blank_ReportsNothing_SoNoTabCanLoseItsLabel()
    {
        var tab = _Tab("Work");
        tab.BeginRename();
        tab.EditName = "   ";

        tab.CommitRename().Should().BeNull();
        tab.Name.Should().Be("Work");
        tab.IsRenaming.Should().BeFalse("the edit still ends — it just changes nothing");
    }

    [Fact]
    public void CommitRename_Unchanged_ReportsNothing_SoAStrayClickDoesNotWriteTheConfig()
    {
        var tab = _Tab("Work");
        tab.BeginRename();

        tab.CommitRename().Should().BeNull();
    }

    [Fact]
    public void CancelRename_DiscardsTheEdit()
    {
        var tab = _Tab("Work");
        tab.BeginRename();
        tab.EditName = "Something else";

        tab.CancelRename();

        tab.IsRenaming.Should().BeFalse();
        tab.Name.Should().Be("Work");
    }

    private static WorkspaceTabViewModel _Tab(string name) =>
        new(Workspace.Create(name, WorkspaceType.Sessions), isActive: true);
}
