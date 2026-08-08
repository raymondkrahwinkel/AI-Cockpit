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
using Cockpit.Core.Worktrees;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-633: the header's "⑂ isolated" badge reads <see cref="SessionPanelViewModel.WorktreeBranch"/>, which the UI's
/// own start/reattach paths are the only writers of — so a session started in a worktree the assistant made through
/// the <c>worktree_create</c> MCP tool showed no badge at all, even though it demonstrably ran in one. That worktree
/// is registered to the pane that asked for it, so the fix matches the registry on the directory the session started
/// in rather than on any session id.
/// </summary>
[Collection("avalonia")]
public class WorktreeBadgeAdoptionTests
{
    private const string Repository = "/repo";

    [Fact]
    public async Task WorkingDirectoryIsARegisteredWorktree_SetsTheBranchEvenWhenAnotherPaneOwnsIt()
    {
        var worktrees = _Registry(new WorktreeRecord(
            "cockpit-assistant", Repository, _Path("wt"), "ac-633-badge", "abc123", DateTimeOffset.UnixEpoch)
        {
            IsAgentCreated = true,
        });

        var (cockpit, session) = _NewSession(worktrees);
        await cockpit._AdoptWorktreeBadgeAsync(session, _Path("wt"));

        Assert.Equal("ac-633-badge", session.WorktreeBranch);
    }

    [Fact]
    public async Task WorkingDirectoryIsNotAWorktree_LeavesTheBadgeOff()
    {
        var worktrees = _Registry(new WorktreeRecord(
            "cockpit-assistant", Repository, _Path("wt"), "ac-633-badge", "abc123", DateTimeOffset.UnixEpoch));

        var (cockpit, session) = _NewSession(worktrees);
        await cockpit._AdoptWorktreeBadgeAsync(session, _Path("plain"));

        Assert.Null(session.WorktreeBranch);
    }

    // The UI-driven paths resolve the branch themselves, before this runs; a second lookup must not talk over them.
    [Fact]
    public async Task BranchAlreadyResolved_IsLeftAlone()
    {
        var worktrees = _Registry(new WorktreeRecord(
            "cockpit-assistant", Repository, _Path("wt"), "ac-633-badge", "abc123", DateTimeOffset.UnixEpoch));

        var (cockpit, session) = _NewSession(worktrees);
        session.WorktreeBranch = "set-at-start";
        await cockpit._AdoptWorktreeBadgeAsync(session, _Path("wt"));

        Assert.Equal("set-at-start", session.WorktreeBranch);
        await worktrees.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    // A registry that throws must cost a badge, not a started session.
    [Fact]
    public async Task RegistryThrows_LeavesTheBadgeOffWithoutThrowing()
    {
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<WorktreeRecord>>(_ => throw new IOException("registry unreadable"));

        var (cockpit, session) = _NewSession(worktrees);
        await cockpit._AdoptWorktreeBadgeAsync(session, _Path("wt"));

        Assert.Null(session.WorktreeBranch);
    }

    private static IWorktreeManager _Registry(params WorktreeRecord[] records)
    {
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns(records);
        return worktrees;
    }

    // Rooted so Path.GetFullPath (which _MatchingWorktreeAsync uses) does not fold in the test runner's own cwd.
    private static string _Path(string leaf) =>
        Path.Combine(Path.GetTempPath(), "ac-633", leaf);

    private static (CockpitViewModel Cockpit, SessionViewModel Session) _NewSession(IWorktreeManager worktrees) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = _NewCockpit(worktrees);
            var session = new SessionViewModel();
            cockpit.Sessions.Add(session);
            return (cockpit, session);
        });

    // Mirrors CloseSessionWorktreeReleaseTests._NewCockpit: the minimum graph plus the worktree manager under test.
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
