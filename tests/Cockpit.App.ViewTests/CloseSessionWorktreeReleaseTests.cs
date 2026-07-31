using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Fix 1 (AC-524's first defect): a worktree an agent creates mid-session through the <c>worktree_create</c> MCP
/// tool never sets <see cref="SessionPanelViewModel.WorktreeBranch"/> — that field is only ever written by the UI's
/// own start/reattach paths. <see cref="CockpitViewModel.CloseSessionAsync"/> and
/// <c>CockpitViewModel._TeardownEmbeddedSessionAsync</c> used to gate <see cref="IWorktreeManager.ReleaseAsync"/> on
/// that field, so such a worktree outlived its own pane's close and sat there until the next startup reconcile.
/// The release call now runs whenever a manager is present, since <see cref="IWorktreeManager.ReleaseAsync"/> itself
/// already scopes to the registry's own records for that session id and is a no-op when it holds none.
/// </summary>
[Collection("avalonia")]
public class CloseSessionWorktreeReleaseTests
{
    [Fact]
    public void CloseSession_ReleasesTheWorktreeEvenWhenWorktreeBranchWasNeverSet()
    {
        var worktreeManager = Substitute.For<IWorktreeManager>();
        var (cockpit, session) = Dispatcher.UIThread.Invoke(() =>
        {
            var c = _NewCockpit(worktreeManager);
            var s = new SessionViewModel();
            c.Sessions.Add(s);
            return (c, s);
        });

        Assert.Null(session.WorktreeBranch);
        Dispatcher.UIThread.Invoke(() => cockpit.CloseSessionCommand.ExecuteAsync(session).GetAwaiter().GetResult());

        worktreeManager.Received(1).ReleaseAsync(session.PaneId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CloseEmbeddedSession_ReleasesTheWorktreeEvenWhenWorktreeBranchWasNeverSet()
    {
        var worktreeManager = Substitute.For<IWorktreeManager>();
        var (cockpit, embedded) = Dispatcher.UIThread.Invoke(() =>
        {
            var c = _NewCockpit(worktreeManager);
            var grid = new SessionViewModel { WorkspaceId = "plugin-desk" };
            c.Sessions.Add(grid);

            var e = c.Embed("plugin-desk", new EmbeddedSessionRequest());
            return (c, e);
        });

        Dispatcher.UIThread.Invoke(() => embedded.CloseAsync().GetAwaiter().GetResult());

        worktreeManager.Received(1).ReleaseAsync(embedded.PaneId, Arg.Any<CancellationToken>());
    }

    // Mirrors WorkspaceAgentGatewayTests._NewEmbeddingCapableCockpit: enough of a graph for Embed(...) to work, plus
    // a worktree manager these tests observe. Kept local rather than shared — that helper lives in a different test
    // file and is private to it.
    private static CockpitViewModel _NewCockpit(IWorktreeManager worktreeManager)
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new Core.Notifications.NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new Core.TranscriptDisplay.TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new Core.SessionBehavior.SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new Core.Layout.LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new Core.Voice.VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new Core.Terminal.TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            sessionProfileStore: Substitute.For<ISessionProfileStore>(),
            worktreeManager: worktreeManager);
    }
}
