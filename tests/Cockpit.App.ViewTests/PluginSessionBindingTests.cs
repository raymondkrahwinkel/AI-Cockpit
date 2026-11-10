using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// <see cref="DesktopBackendHost.BindToSession"/> (AC-832): the seam that ties a plugin surface to a session already
/// running. Checked on the ownership rule — sending reaches that session, and closing either side leaves the other.
/// </summary>
[Collection("avalonia")]
public class PluginSessionBindingTests
{
    [Fact]
    public void SendAsync_ReachesTheSessionThatWasAlreadyRunning() => Dispatcher.UIThread.Invoke(() =>
    {
        // AC-2256: the text goes out as a turn of its own and the operator's draft stays where it was.
        var control = Substitute.For<ISessionControl>();
        control.IsRunning.Returns(true);
        var controls = Substitute.For<ISessionControlFactory>();
        controls.Create(Arg.Any<Func<string>>()).Returns(control);
        var running = new SessionViewModel(controls) { InputText = "half a thought" };
        var (host, cockpit, session) = _HostWithOneSession(running);
        using var binding = host.BindToSession(session.PaneId);

        binding.SendAsync("Pin this shape to the conversation.").GetAwaiter().GetResult();

        Assert.True(binding.IsLive);
        control.Received(1).SubmitAsync(Arg.Is<QueuedPrompt>(prompt => prompt.Text == "Pin this shape to the conversation."), Arg.Any<bool>());
        Assert.Equal("half a thought", session.InputText);
        Assert.Same(session, Assert.Single(cockpit.Sessions));
    });

    [Fact]
    public void ClosingTheSession_EndsTheBindingWithoutClosingTheSurface() => Dispatcher.UIThread.Invoke(() =>
    {
        var (host, cockpit, session) = _HostWithOneSession();
        using var binding = host.BindToSession(session.PaneId);
        var ended = 0;
        binding.Ended += (_, _) => ended++;

        cockpit.CloseSessionCommand.ExecuteAsync(session).GetAwaiter().GetResult();

        Assert.Equal(1, ended);
        Assert.False(binding.IsLive);
        Assert.Null(binding.SessionName);
        Assert.Equal(session.PaneId, binding.PaneId);

        // The surface is still there and still knows which session it belonged to; sending is simply refused.
        binding.SendAsync("Anyone still there?").GetAwaiter().GetResult();
    });

    // AC-1392: the plugin host looks a pane up before it acts, and the pane can close in between; the handle asks
    // again inside the UI callback that acts, so a pane no longer live takes nothing.
    [Fact]
    public async Task APaneHandle_WhoseSessionIsNoLongerLive_TakesNoTextAndNoName()
    {
        var (sent, named, inputText, title, originalTitle) = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var session = new SessionViewModel();
            var originalTitle = session.Title;
            var handle = new SessionPanelHandle(session, isEmbedded: false, () => null, isLive: () => false);

            var sent = await handle.InjectAndSubmitAsync("anyone there?");
            var named = await handle.SetNameAsync("AC-1392");
            return (sent, named, session.InputText, session.Title, originalTitle);
        });

        Assert.Equal((false, false, string.Empty, originalTitle), (sent, named, inputText, title));
    }

    private static (ICockpitHost Host, CockpitViewModel Cockpit, SessionViewModel Session) _HostWithOneSession(SessionViewModel? existing = null)
    {
        // AC-1392: the host reaches the pane through the registry the cockpit feeds and the desktop's launcher.
        var registry = new SessionRegistry();
        var cockpit = _NewCockpit(registry);
        var session = existing ?? new SessionViewModel();
        cockpit.Sessions.Add(session);

        var services = new ServiceCollection()
            .AddSingleton(cockpit)
            .AddSingleton<ISessionRegistry>(registry)
            .AddSingleton<ISessionLauncher>(DesktopLauncher.Over(cockpit, registry))
            .BuildServiceProvider();
        var host = new DesktopBackendHost(
            "diagram",
            "Diagram",
            services,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            new PluginSessionObserver(cockpit),
            new PluginDiagnostics());

        return (host, cockpit, session);
    }

    private static CockpitViewModel _NewCockpit(SessionRegistry registry)
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
            sessionRegistry: registry);
    }
}
