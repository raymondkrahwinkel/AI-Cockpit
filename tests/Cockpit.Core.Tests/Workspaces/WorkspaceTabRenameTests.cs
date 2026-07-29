using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;

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

        Assert.True(tab.IsRenaming);
        Assert.Equal("Work", tab.EditName);
    }

    [Fact]
    public void CommitRename_ReportsTheNewName_AndShowsItImmediately()
    {
        var tab = _Tab("Work");
        tab.BeginRename();
        tab.EditName = "Client work";

        var committed = tab.CommitRename();

        Assert.Equal("Client work", committed);
        Assert.Equal("Client work", tab.Name);
        Assert.False(tab.IsRenaming);
    }

    [Fact]
    public void CommitRename_TrimsWhitespace()
    {
        var tab = _Tab("Work");
        tab.BeginRename();
        tab.EditName = "  Client work  ";

        Assert.Equal("Client work", tab.CommitRename());
    }

    [Fact]
    public void CommitRename_Blank_ReportsNothing_SoNoTabCanLoseItsLabel()
    {
        var tab = _Tab("Work");
        tab.BeginRename();
        tab.EditName = "   ";

        Assert.Null(tab.CommitRename());
        Assert.Equal("Work", tab.Name);
        Assert.False(tab.IsRenaming, "the edit still ends — it just changes nothing");
    }

    [Fact]
    public void CommitRename_Unchanged_ReportsNothing_SoAStrayClickDoesNotWriteTheConfig()
    {
        var tab = _Tab("Work");
        tab.BeginRename();

        Assert.Null(tab.CommitRename());
    }

    [Fact]
    public void CancelRename_DiscardsTheEdit()
    {
        var tab = _Tab("Work");
        tab.BeginRename();
        tab.EditName = "Something else";

        tab.CancelRename();

        Assert.False(tab.IsRenaming);
        Assert.Equal("Work", tab.Name);
    }

    private static WorkspaceTabViewModel _Tab(string name) =>
        new(Workspace.Create(name, WorkspaceType.Sessions), isActive: true);
}
