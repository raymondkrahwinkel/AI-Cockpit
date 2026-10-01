using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Journeys;

// The product's core act on the desktop: a session started from a project and a profile, a prompt through its
// composer and the answer in its pane. The route the operator takes, through the real New session dialog.
[Collection(JourneyCollection.Alone)]
public sealed class SessionJourney
{
    // J2: the dialog opens the pane, the composer sends, and the provider's answer is a row in that pane, with the
    // status going busy and then done around it (AC-1437). A turn that fails offers Retry on its row (AC-728), which
    // the pane can only do once that row came in from the event log (AC-1438).
    [Fact]
    public async Task ASessionStartedFromAProjectAndAProfile_AnswersInItsPane()
    {
        await using var cockpit = JourneyHost.Desktop();
        var project = await cockpit.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(cockpit.StateRoot, "project")).FullName);
        await cockpit.StartDesktopAsync();

        var session = await cockpit.StartSessionThroughTheDialogAsync(project);
        var statuses = new List<SessionStatus>();
        TranscriptEntryViewModel? failed = null;
        await HeadlessAvalonia.RunAsync(async () =>
        {
            session.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(session.SessionStatus))
                {
                    statuses.Add(session.SessionStatus);
                }
            };
            session.InputText = "hello";
            await session.SendCommand.ExecuteAsync(null);
            await Until.CollectionHolds(session.Transcript, () => session.Transcript.Any(row => row.Kind == TranscriptEntryKind.AssistantText && row.Text == "echo: hello"));
            await Until.Holds(session, () => session.SessionStatus == SessionStatus.Done);

            session.InputText = EchoDriver.FailingPrompt;
            await session.SendCommand.ExecuteAsync(null);
            await Until.Holds(session, () => (failed = session.Transcript.LastOrDefault(row => row.IsFailedTurnRow))?.ActionLabel == "Retry");
        });

        Assert.Equal(JourneyHost.EchoProfile, cockpit.Driver.Profile?.Label);
        Assert.Equal([SessionStatus.Busy, SessionStatus.Done], statuses.Take(2));
        Assert.NotNull(failed);
        Assert.Contains(session, HeadlessAvalonia.Run(() => cockpit.Cockpit.Sessions.ToList()));
    }

    // J3: in a project that is a git repository and isolates by default, the session runs in a worktree on a branch
    // of its own, and closing the pane takes both away again.
    [Fact]
    public async Task AWorktreeSession_GetsItsOwnWorktree_AndClosingThePaneRemovesItAndItsBranch()
    {
        await using var cockpit = JourneyHost.Desktop();
        var repository = await _RepositoryAsync(Path.Combine(cockpit.StateRoot, "repository"));
        var project = await cockpit.SaveProfileAndProjectAsync(repository, isolateInWorktree: true);
        await cockpit.StartDesktopAsync();
        var worktrees = cockpit.Services.GetRequiredService<IWorktreeManager>();

        var session = await cockpit.StartSessionThroughTheDialogAsync(project);
        var worktree = (await worktrees.ListAsync()).Single(record => record.SessionId == session.PaneId);
        var branchWhileOpen = await _BranchesAsync(repository, worktree.Branch);
        await HeadlessAvalonia.RunAsync(() => cockpit.Cockpit.CloseSessionCommand.ExecuteAsync(session));

        Assert.Equal(worktree.Path, cockpit.Driver.WorkingDirectory);
        Assert.NotEqual(Path.GetFullPath(repository), Path.GetFullPath(worktree.Path));
        Assert.NotEmpty(branchWhileOpen);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Empty(await _BranchesAsync(repository, worktree.Branch));
    }

    // A repository with one commit, the least a worktree can branch from.
    private static async Task<string> _RepositoryAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await _GitAsync(folder, "init", "-b", "main");
        await _GitAsync(folder, "-c", "user.name=Journey", "-c", "user.email=journey@localhost", "commit", "--allow-empty", "-m", "initial");
        return folder;
    }

    private static async Task<string> _BranchesAsync(string repository, string branch) =>
        (await _GitAsync(repository, "branch", "--list", branch)).Trim();

    private static async Task<string> _GitAsync(string folder, params string[] arguments)
    {
        var result = await GitCli.RunAsync(folder, arguments, CancellationToken.None);
        return result.ExitCode == 0 ? result.StandardOutput : throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {result.StandardError}");
    }
}
