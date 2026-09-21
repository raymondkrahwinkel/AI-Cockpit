using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1343: a session pane restored before <c>CockpitViewModel.UsageThresholds</c> is set reads null and is never
/// revisited — the field is assigned once, from <c>App._LoadUsageThresholdsAsync</c>. <see cref="App.RestoreCockpitAsync"/>
/// takes that load as a task and waits it out before touching the workspace store, so restore never races ahead of it.
/// </summary>
public sealed class Ac1343RestoreOrderTests
{
    [Fact]
    public async Task RestoreCockpitAsync_WaitsForThresholdsLoaded_BeforeLoadingWorkspaces()
    {
        var thresholdsLoaded = new TaskCompletionSource();
        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(WorkspaceSettings.Default);
        var cockpit = _Cockpit(workspaceStore);

        var restore = App.RestoreCockpitAsync(cockpit, thresholdsLoaded.Task);

        _ = workspaceStore.DidNotReceive().LoadAsync(Arg.Any<CancellationToken>());

        thresholdsLoaded.SetResult();
        await restore;

        _ = workspaceStore.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    private static CockpitViewModel _Cockpit(IWorkspaceSettingsStore workspaceStore)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminal = Substitute.For<ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcriptDisplay,
            sessionBehavior,
            layout,
            voice,
            terminal,
            workspaceSettingsStore: workspaceStore);
    }
}
