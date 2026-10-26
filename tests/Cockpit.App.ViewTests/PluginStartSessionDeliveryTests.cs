using Cockpit.App.Services;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public sealed class PluginStartSessionDeliveryTests
{
    [Fact]
    public Task PluginSessionActions_HoldTheFirstTurnAndFocusOnlyExistingPanes() => HeadlessAvalonia.RunAsync(async () =>
    {
        var registry = new SessionRegistry();
        var launcher = new DesktopLauncher.Slot();
        var cockpit = _Cockpit(registry, launcher);
        launcher.Launcher = DesktopLauncher.Over(cockpit, registry);

        await cockpit.StartSessionForPluginAsync(
            new SessionProfile("Codex", new ClaudeConfig("/fake/.claude")),
            "Book the confirmed week proposal.",
            null,
            "Hours week 43");

        var session = Assert.IsType<SessionViewModel>(Assert.Single(cockpit.Sessions));
        Assert.True(session.HasPromptWaitingToBeDelivered);
        Assert.Equal(string.Empty, session.InputText);

        var other = new SessionViewModel();
        cockpit.Sessions.Add(other);
        cockpit.SimpleView = false;
        cockpit.SelectedSession = other;
        var surfaces = new RecordingUiSurfaces();
        using var services = new ServiceCollection().AddSingleton(cockpit)
            .AddSingleton(new PluginChannelHub(NullLogger<PluginChannelHub>.Instance)).BuildServiceProvider();
        ICockpitUiHost uiHost = new CockpitUiHost("test", "Test", Substitute.For<ICockpitHost>(), services, surfaces, surfaces, surfaces, surfaces, []);

        Assert.True(await Task.Run(() => uiHost.FocusSessionAsync(session.PaneId)));
        Assert.Same(session, cockpit.SelectedSession);
        Assert.False(await uiHost.FocusSessionAsync("closed-pane"));
        Assert.Same(session, cockpit.SelectedSession);
        cockpit.SimpleView = true;
        cockpit.SimpleSelectedSession = other;
        Assert.True(await uiHost.FocusSessionAsync(session.PaneId));
        Assert.Same(session, cockpit.SimpleSelectedSession);
        Assert.True(session.HasPromptWaitingToBeDelivered);
        Assert.Equal(string.Empty, session.InputText);
    });

    private static CockpitViewModel _Cockpit(SessionRegistry registry, DesktopLauncher.Slot launcher)
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
            sessionRegistry: registry,
            sessionLauncher: launcher.Get);
    }
}
