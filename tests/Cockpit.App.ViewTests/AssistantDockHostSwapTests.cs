using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Docking;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-953's standing assumption: one <see cref="AssistantChatViewModel"/>, in exactly one host at a time. It broke
/// the first time it met an operator — docking from the rail tab left the floating window standing beside the
/// docked panel, two chat surfaces with their own composers, and the header button then read the wrong way round
/// because the flag it toggles was already set. These drive the coordinator's own host choice, which is where that
/// decision lives.
/// </summary>
[Collection("avalonia")]
public sealed class AssistantDockHostSwapTests
{
    // The coordinator's dependencies are all interfaces or thin wrappers around them, so the real class is what
    // runs here — the point is its host choice, and a stand-in for that would be testing the stand-in.
    internal static (AssistantIndicatorCoordinator Coordinator, CockpitViewModel Cockpit, IDockPanelRegistry Panels) Build(
        Microsoft.Extensions.Logging.ILogger<AssistantIndicatorCoordinator>? logger = null)
    {
        // One registry, shared by the cockpit and the coordinator, exactly as the container hands it out — with two
        // of them the rail lists nothing and every assertion about it would pass for the wrong reason.
        var panels = new DockPanelRegistry();
        var cockpit = new CockpitViewModel(panels);

        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettings());

        var sessionState = Substitute.For<ISessionStateStore>();
        var sessionStateRecorder = new SessionStateRecorder(
            sessionState,
            new SessionConversationTracker(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionStateRecorder>.Instance);

        var assistant = new AssistantSessionHost(
            cockpit,
            settings,
            Substitute.For<IAssistantProfileStore>(),
            sessionState,
            sessionStateRecorder,
            Substitute.For<IMcpServerCatalog>(),
            Substitute.For<IAssistantMemory>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<AssistantSessionHost>>());

        var overlay = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), Substitute.For<IVoiceOverlayPresenter>());
        var openMic = new OpenMicCoordinator(
            Substitute.For<IOpenMicListener>(),
            assistant,
            Substitute.For<IVoiceSettingsStore>(),
            settings,
            Substitute.For<IVoicePlaybackQueue>(),
            overlay,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<OpenMicCoordinator>>());

        var coordinator = new AssistantIndicatorCoordinator(
            assistant,
            openMic,
            overlay,
            settings,
            Substitute.For<IVoicePlaybackQueue>(),
            Substitute.For<IAssistantSpawnAuditLog>(),
            cockpit,
            panels,
            logger);

        coordinator.Start();
        return (coordinator, cockpit, panels);
    }

    // What the dock rail itself does with the registration: builds the panel's view. Standing in for
    // CockpitView's own _RebuildDockPanelContent, which is a view detail — the registration is the seam.
    private static Control _OpenTheRailPanel(IDockPanelRegistry panels) =>
        panels.Panels.Single(panel => panel.Id == AssistantIndicatorCoordinator.DockPanelId).CreateView();

    [Fact]
    public async Task DockingFromTheWindow_TakesTheWindowAway_AndOnlyThenIsThereARailTab()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, panels) = Build();

            // The operator opens the chat the ordinary way: the sidebar chip, undocked, so a window.
            coordinator.Indicator.ClickCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var floating = coordinator.OpenChatWindow;
            Assert.NotNull(floating);
            Assert.Empty(cockpit.DockPanels);

            // The header's Dock button.
            var chat = (AssistantChatViewModel)floating!.DataContext!;
            chat.ToggleDockCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(coordinator.OpenChatWindow);
            Assert.True(cockpit.AssistantDocked, "a dock the operator can see has to be the dock that is remembered");

            // One view model, two views over its life — never two conversations.
            var docked = (AssistantChatView)_OpenTheRailPanel(panels);
            Assert.Same(chat, docked.DataContext);
            Assert.True(chat.IsDocked, "the header reads this to show Undock");
        });
    }

    [Fact]
    public async Task WithTheAssistantDocked_TheChipOpensTheRail_AndNeverASecondWindow()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, _) = Build();
            await cockpit.SetAssistantDockedAsync(true, AssistantIndicatorCoordinator.DockPanelId);

            coordinator.Indicator.ClickCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(coordinator.OpenChatWindow);
            Assert.Equal(AssistantIndicatorCoordinator.DockPanelId, cockpit.OpenDockPanelId);
        });
    }

    [Fact]
    public async Task Undocking_ClosesTheRailPanel_BeforeTheWindowTakesTheChat()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var lines = new List<string>();
            var (coordinator, cockpit, panels) = Build(new _CollectingLogger(lines));
            await cockpit.SetAssistantDockedAsync(true, AssistantIndicatorCoordinator.DockPanelId);

            var docked = (AssistantChatView)_OpenTheRailPanel(panels);
            var chat = (AssistantChatViewModel)docked.DataContext!;
            Assert.True(chat.IsDocked);

            // The header's Undock button.
            chat.ToggleDockCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(chat.IsDocked);
            Assert.False(cockpit.AssistantDocked);
            Assert.Null(cockpit.OpenDockPanelId);

            // AC-1256: the freeze this line exists for started seconds after an undock, and the log held no record
            // that one had happened. Asserted here rather than in its own test because this is the undock.
            Assert.Contains(lines, line => line.Contains("moving to its own window", StringComparison.Ordinal));

            var floating = coordinator.OpenChatWindow;
            Assert.NotNull(floating);
            Assert.Same(chat, floating!.DataContext);
        });
    }

    // Only the formatted message is kept: what the assertion above is about is that the swap says so at all.
    private sealed class _CollectingLogger(List<string> lines) : Microsoft.Extensions.Logging.ILogger<AssistantIndicatorCoordinator>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => _Scope.Instance;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));

        private sealed class _Scope : IDisposable
        {
            public static readonly _Scope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    // The same undock, but through the real rail rather than the registration alone: the settings saying the panel
    // is closed is not the same claim as the docked chat being off the screen, and it was the second one the
    // operator was looking at when they reported a window and a docked panel standing side by side.
    [Fact]
    public async Task Undocking_TakesTheDockedChatOutOfTheCockpitsOwnTree_NotJustOutOfTheSettings()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, _) = Build();

            var main = new Window { Width = 1100, Height = 760, Content = new CockpitView { DataContext = cockpit } };
            main.Show();
            main.UpdateLayout();

            try
            {
                // Docked, so the rail carries the tab and its panel is open — the state a restart restores into.
                await cockpit.SetAssistantDockedAsync(true, AssistantIndicatorCoordinator.DockPanelId);
                Dispatcher.UIThread.RunJobs();
                main.UpdateLayout();

                var docked = main.GetVisualDescendants().OfType<AssistantChatView>().SingleOrDefault();
                Assert.NotNull(docked);

                ((AssistantChatViewModel)docked!.DataContext!).ToggleDockCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                main.UpdateLayout();

                Assert.Empty(main.GetVisualDescendants().OfType<AssistantChatView>());
                Assert.NotNull(coordinator.OpenChatWindow);

                // And the tab goes with it: undocked, the assistant is a window, so a rail tab for it would only
                // ever open a second one. With nothing left registered the rail gives its column back entirely
                // rather than standing there as an empty strip.
                Assert.Empty(cockpit.DockPanels);
                Assert.False(cockpit.HasDockPanels);
            }
            finally
            {
                main.Close();
            }
        });
    }
}
