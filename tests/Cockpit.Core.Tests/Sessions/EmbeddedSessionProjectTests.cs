using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Core.Worktrees;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// Which project a session a plugin embeds belongs to (AC-320). Every embedded session used to belong to none —
/// only the New-session routes set one — so a plugin's per-project contribution to a starting session (AC-165) did
/// not fire for the autonomous run that needed it most.
/// </summary>
public class EmbeddedSessionProjectTests
{
    private static Project _At(string name, string source) =>
        new(name.ToLowerInvariant(), name) { SourceDirectory = source };

    private static WorktreeRecord _Worktree(string path, string repository) =>
        new("pane", repository, path, "autopilot/run", "abc1234", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Resolve_AStepPointedAtTheCheckout_IsThatProject()
    {
        // The Autopilot step's own shape: it runs in the repository directory and isolates from there.
        var project = EmbeddedSessionProject.Resolve([_At("Cockpit", "/repos/cockpit")], [], "/repos/cockpit");
        Assert.NotNull(project);
        Assert.Equal("Cockpit", project.Name);
    }

    [Fact]
    public void Resolve_ARunPointedStraightAtItsWorktree_IsTheRepositorysProject()
    {
        // The validating CEO's shape: it is pointed at the run's worktree so it can read the accumulated work.
        var project = EmbeddedSessionProject.Resolve(
                [_At("Cockpit", "/repos/cockpit")],
                [_Worktree("/worktrees/run-1", "/repos/cockpit")],
                "/worktrees/run-1");
        Assert.NotNull(project);
        Assert.Equal("Cockpit", project.Name);
    }

    [Fact]
    public void Resolve_TheStepAndTheValidatorOfOneRun_AgreeOnTheProject()
    {
        Project[] projects = [_At("Cockpit", "/repos/cockpit")];
        WorktreeRecord[] worktrees = [_Worktree("/worktrees/run-1", "/repos/cockpit")];

        var step = EmbeddedSessionProject.Resolve(projects, worktrees, "/repos/cockpit");
        var validator = EmbeddedSessionProject.Resolve(projects, worktrees, "/worktrees/run-1");

        Assert.Same(step, validator);
    }

    [Fact]
    public void Resolve_AWorktreeCutFromAWorktree_StillReachesTheProject()
    {
        // A run started from a session that is itself isolated: git inside a linked worktree reports that worktree as
        // the repository, so the record chains. One hop would land on a folder no project claims.
        var project = EmbeddedSessionProject.Resolve(
                [_At("Cockpit", "/repos/cockpit")],
                [_Worktree("/worktrees/session-1", "/repos/cockpit"), _Worktree("/worktrees/run-1", "/worktrees/session-1")],
                "/worktrees/run-1");
        Assert.NotNull(project);
        Assert.Equal("Cockpit", project.Name);
    }

    [Fact]
    public void Resolve_ARegistryThatPointsAtItself_AnswersRatherThanSpins() =>
        // A cycle costs the answer, never the session: the walk must not loop.
        Assert.Null(EmbeddedSessionProject.Resolve(
                [_At("Cockpit", "/repos/cockpit")],
                [_Worktree("/worktrees/a", "/worktrees/b"), _Worktree("/worktrees/b", "/worktrees/a")],
                "/worktrees/a"));

    [Fact]
    public void Resolve_AWorktreeOfARepositoryNoProjectClaims_IsNoProject() =>
        Assert.Null(EmbeddedSessionProject.Resolve(
                [_At("Cockpit", "/repos/cockpit")],
                [_Worktree("/worktrees/run-1", "/repos/something-else")],
                "/worktrees/run-1"));

    [Fact]
    public void Resolve_AFolderInsideAWorktree_IsNotTheWorktree() =>
        // A worktree is a checkout, not a folder tree the host owns: only the worktree itself maps back.
        Assert.Null(EmbeddedSessionProject.Resolve(
                [_At("Cockpit", "/repos/cockpit")],
                [_Worktree("/worktrees/run-1", "/repos/cockpit")],
                "/worktrees/run-1/src"));

    [Fact]
    public void Resolve_AProjectScopedWideEnoughToContainTheWorktrees_DoesNotClaimTheRun()
    {
        // The one that matters: worktrees live under the cockpit's own state folder, so a project on the home
        // directory would otherwise claim every isolated run in the cockpit and hand it the wrong repository.
        var project = EmbeddedSessionProject.Resolve(
                [_At("Home", "/home/raymond"), _At("Cockpit", "/repos/cockpit")],
                [_Worktree("/home/raymond/.config/Cockpit/worktrees/run-1", "/repos/cockpit")],
                "/home/raymond/.config/Cockpit/worktrees/run-1");
        Assert.NotNull(project);
        Assert.Equal("Cockpit", project.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_WithoutADirectory_IsNoProject(string? directory) =>
        Assert.Null(EmbeddedSessionProject.Resolve([_At("Cockpit", "/repos/cockpit")], [], directory));
}
