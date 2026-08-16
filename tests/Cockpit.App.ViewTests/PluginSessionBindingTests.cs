using Avalonia.Threading;
using Cockpit.App.Plugins;
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
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// <see cref="CockpitHost.BindToSession"/> (AC-832): the seam that ties a plugin surface to a session that is
/// already running. Checked on the two halves the ownership rule is made of — text sent through the binding lands
/// in that existing session, and neither closing the binding nor closing the session takes the other down with it.
/// </summary>
[Collection("avalonia")]
public class PluginSessionBindingTests
{
    [Fact]
    public void SendAsync_ReachesTheSessionThatWasAlreadyRunning() => Dispatcher.UIThread.Invoke(() =>
    {
        var (host, cockpit, session) = _HostWithOneSession();
        using var binding = host.BindToSession(session.PaneId);

        binding.SendAsync("Pin this shape to the conversation.").GetAwaiter().GetResult();

        Assert.True(binding.IsLive);
        Assert.Equal("Pin this shape to the conversation.", session.InputText);
        Assert.Same(session, Assert.Single(cockpit.Sessions));
    });

    [Fact]
    public void Dispose_LeavesTheSessionRunning() => Dispatcher.UIThread.Invoke(() =>
    {
        var (host, cockpit, session) = _HostWithOneSession();
        var binding = host.BindToSession(session.PaneId);

        binding.Dispose();

        Assert.Same(session, Assert.Single(cockpit.Sessions));
        Assert.True(host.BindToSession(session.PaneId).IsLive);
    });

    [Fact]
    public void BindToSession_TwiceOnOneSession_AddsNoSecondPane() => Dispatcher.UIThread.Invoke(() =>
    {
        var (host, cockpit, session) = _HostWithOneSession();

        using var first = host.BindToSession(session.PaneId);
        using var second = host.BindToSession(session.PaneId);

        // Both peepholes onto the one pane the grid draws: no second SessionPanelViewModel, so no rival view over
        // its pty (CockpitViewModel.cs:7026).
        Assert.Same(session, Assert.Single(cockpit.Sessions));
        Assert.Equal(first.PaneId, second.PaneId);
        Assert.Equal(session.Title, second.SessionName);
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

    [Fact]
    public void BindToSession_WithAPaneIdNoSessionIsBehind_IsNotLiveRatherThanAThrow() => Dispatcher.UIThread.Invoke(() =>
    {
        var (host, _, _) = _HostWithOneSession();

        using var binding = host.BindToSession("a-pane-that-never-existed");

        Assert.False(binding.IsLive);
        Assert.Null(binding.SessionName);
        Assert.Equal("a-pane-that-never-existed", binding.PaneId);
        binding.SendAsync("Draw a box.").GetAwaiter().GetResult();
    });

    private static (ICockpitHost Host, CockpitViewModel Cockpit, SessionViewModel Session) _HostWithOneSession()
    {
        var cockpit = _NewCockpit();
        var session = new SessionViewModel();
        cockpit.Sessions.Add(session);

        var services = new ServiceCollection().AddSingleton(cockpit).BuildServiceProvider();
        var host = new CockpitHost(
            "diagram",
            "Diagram",
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            new PluginSessionObserver(cockpit),
            new PluginDiagnostics());

        return (host, cockpit, session);
    }

    private static CockpitViewModel _NewCockpit()
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
            sessionProfileStore: Substitute.For<ISessionProfileStore>());
    }
}
