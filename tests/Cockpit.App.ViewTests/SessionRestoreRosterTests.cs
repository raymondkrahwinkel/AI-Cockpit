using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Assistant;
using Cockpit.Core.Workspaces;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-410: <see cref="SessionRestoreRoster"/> is the one place that answers "which AI-session panes will this
/// start offer back" — the startup worktree reconcile and the session-state compaction both use it, so they
/// cannot each derive a different set of pane ids and drift apart.
/// </summary>
public class SessionRestoreRosterTests
{
    [Fact]
    public void Panes_GivesOnlyAiSessionPanesOnSessionsWorkspaces_AndNothingElse()
    {
        var aiSessionPane = new WorkspacePane("ai-1", PaneKind.AiSession);
        var terminalPane = new WorkspacePane("term-1", PaneKind.Terminal);
        var sessionsWorkspace = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(aiSessionPane).WithPane(terminalPane);

        var dashboardWorkspace = Workspace.Create("Board", WorkspaceType.Dashboard);
        var settings = new WorkspaceSettings { Workspaces = [sessionsWorkspace, dashboardWorkspace], ActiveWorkspaceId = sessionsWorkspace.Id };

        var result = SessionRestoreRoster.Panes(settings).ToList();

        var entry = Assert.Single(result);
        Assert.Equal("ai-1", entry.Pane.Id);
        Assert.Equal(sessionsWorkspace.Id, entry.Workspace.Id);
    }

    /// <summary>
    /// AC-1089: the assistant belongs in this set even though <see cref="SessionRestoreRoster.Panes"/> cannot see it
    /// — it is live on every start but owns no workspace pane, so both callers read it as gone: the worktree
    /// reconcile swept the worktrees it made with <c>worktree_create</c> (AC-654 already knew, for the periodic
    /// sweep only) and the state compaction dropped its saved conversation id, which is why resume found nothing.
    /// </summary>
    [Fact]
    public async Task PaneIdsAsync_ReadsTheStore_AndGivesThePanesIdsPlusTheAssistant()
    {
        var aiSessionPane = new WorkspacePane("ai-1", PaneKind.AiSession);
        var sessionsWorkspace = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(aiSessionPane);
        var settings = new WorkspaceSettings { Workspaces = [sessionsWorkspace], ActiveWorkspaceId = sessionsWorkspace.Id };

        var store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var ids = await SessionRestoreRoster.PaneIdsAsync(store);

        Assert.Equal(new HashSet<string> { "ai-1", AssistantIdentity.PaneId }, ids);
    }
}
