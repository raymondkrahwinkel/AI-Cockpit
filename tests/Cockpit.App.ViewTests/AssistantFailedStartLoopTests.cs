using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.Docking;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Sessions;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cockpit.App.ViewTests;

// A restored config on a clean machine (a Fedora backup on Windows, a provider plugin that is not installed, a CLI
// that is not there) makes the assistant's start fail every time. Measured on the full CockpitView in the Simple
// stand: such a start is tried once, says why, and is then left alone until the operator does something.
[Collection("avalonia")]
public sealed class AssistantFailedStartLoopTests
{
    public static TheoryData<string, Action<ISessionDriverFactory, ISessionDriver>, string> StructuralFailures => new()
    {
        {
            "provider plugin not installed",
            (factory, _) => factory.Create(Arg.Any<SessionProfile?>())
                .Throws(new InvalidOperationException("No session provider is registered for 'claude'. Install the plugin that provides it.")),
            "No session provider is registered for 'claude'"
        },
        {
            "CLI at a Linux path on Windows",
            (_, driver) => driver.StartAsync(
                    Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
                    Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Throws(new System.ComponentModel.Win32Exception(2, "An error occurred trying to start process '/home/raymond/.local/bin/claude'. The system cannot find the file specified.")),
            "/home/raymond/.local/bin/claude"
        },
    };

    [Theory]
    [MemberData(nameof(StructuralFailures))]
    public async Task AStartThatCannotSucceed_IsTriedOnce_AndSaysWhy(
        string failure, Action<ISessionDriverFactory, ISessionDriver> arrange, string reasonFragment) => await HeadlessAvalonia.RunAsync(async () =>
    {
        var stand = _StandUp(arrange);
        try
        {
            await stand.PumpAsync();

            Assert.True(stand.Starts == 1, $"{failure}: the start was tried {stand.Starts} times and {stand.Views} chat views were built");
            Assert.True(stand.Views == 1, $"{failure}: {stand.Views} chat views were built for one failed start");
            Assert.Null(stand.Host.Session);
            Assert.True(stand.Cockpit.AssistantChat!.IsUnavailable);
            Assert.Contains(reasonFragment, stand.Cockpit.AssistantChat.UnavailableReason);
        }
        finally
        {
            stand.Dispose();
        }
    });

    // AC-1316's retry stays: the operator typing is the one thing that tries the start again after it failed.
    [Fact]
    public async Task TypingAfterAFailedStart_TriesAgain() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var stand = _StandUp((factory, _) => factory.Create(Arg.Any<SessionProfile?>()).Throws(new InvalidOperationException("no provider")));
        try
        {
            await stand.PumpAsync();
            Assert.Equal(1, stand.Starts);

            var chat = stand.Cockpit.AssistantChat!;
            chat.InputText = "hello";
            await chat.SendCommand.ExecuteAsync(null);
            await stand.PumpAsync();

            Assert.Equal(2, stand.Starts);
            Assert.Equal("hello", chat.InputText);
            Assert.True(chat.IsUnavailable);
        }
        finally
        {
            stand.Dispose();
        }
    });

    // The whole graph between the cockpit and the chat view, real: the host, the coordinator (which owns the
    // Simple stand's chat-view factory), the dock registry the rail reads and the CockpitView itself.
    private static _Stand _StandUp(Action<ISessionDriverFactory, ISessionDriver> arrange)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_NoEvents());
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        arrange(factory, driver);

        var stand = new _Stand();
        var panels = new DockPanelRegistry();
        var cockpit = _Cockpit(() =>
        {
            // A loop here never yields the UI thread back, so it is cut after ten turns to let the count be read.
            if (++stand.Starts > 10)
            {
                throw new InvalidOperationException("the assistant is being started in a loop");
            }

            return new SessionViewModel(new SessionManager(factory));
        }, panels);
        stand.Cockpit = cockpit;
        stand.Panels = panels;
        stand.Host = _Wire(cockpit, panels, new SessionProfile("assistant", new ClaudeConfig("/home/raymond/.local/bin/claude")));
        var chatViewFactory = cockpit.CreateSimpleViewChatView!;
        cockpit.CreateSimpleViewChatView = () =>
        {
            stand.Views++;
            return chatViewFactory();
        };

        stand.Window = new Window { Width = 1100, Height = 760, Content = new CockpitView { DataContext = cockpit } };
        stand.Window.Show();
        return stand;
    }

    private sealed class _Stand : IDisposable
    {
        public int Starts;
        public int Views;
        public CockpitViewModel Cockpit = null!;
        public DockPanelRegistry Panels = null!;
        public AssistantSessionHost Host = null!;
        public Window Window = null!;

        // Two seconds of a live UI thread: the loop, when there is one, turns dozens of times in that.
        public async Task PumpAsync()
        {
            for (var i = 0; i < 40; i++)
            {
                Window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(50);
            }
        }

        public void Dispose()
        {
            Window.Close();
            Panels.Unregister(AssistantIndicatorCoordinator.DockPanelId);
        }
    }

    private static AssistantSessionHost _Wire(CockpitViewModel cockpit, IDockPanelRegistry panels, SessionProfile profile)
    {
        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettings { IsEnabled = true });
        var profiles = Substitute.For<IAssistantProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantProfileSlot(profile));
        var sessionState = Substitute.For<ISessionStateStore>();
        sessionState.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);
        sessionState.TryLoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<SessionStateRecord>?>([]);
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<McpServerConfig>>([]);

        var host = new AssistantSessionHost(
            new SessionLauncherAdapter(cockpit), new NodeControllerPresence(), settings, profiles, sessionState,
            new SessionStateRecorder(sessionState, new SessionConversationTracker(), NullLogger<SessionStateRecorder>.Instance),
            catalog, Substitute.For<IAssistantMemory>(), NullLogger<AssistantSessionHost>.Instance);

        var overlay = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), Substitute.For<IVoiceOverlayPresenter>());
        var openMic = new OpenMicCoordinator(
            Substitute.For<IOpenMicListener>(), host, Substitute.For<IVoiceSettingsStore>(), settings,
            Substitute.For<IVoicePlaybackQueue>(), overlay, NullLogger<OpenMicCoordinator>.Instance);
        new AssistantIndicatorCoordinator(
            host, openMic, overlay, settings, Substitute.For<IVoicePlaybackQueue>(),
            Substitute.For<IAssistantSpawnAuditLog>(), cockpit, panels).Start();

        return host;
    }

    private static CockpitViewModel _Cockpit(Func<SessionViewModel> sessionFactory, IDockPanelRegistry panels)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcripts = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcripts.LoadAsync().Returns(new TranscriptDisplaySettings());
        var behavior = Substitute.For<ISessionBehaviorSettingsStore>();
        behavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminals = Substitute.For<ITerminalSettingsStore>();
        terminals.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            sessionFactory,
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications, transcripts, behavior, layout, voice, terminals,
            dockPanelRegistry: panels)
        {
            SimpleView = true,
        };
    }

    private static async IAsyncEnumerable<SessionEvent> _NoEvents([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
