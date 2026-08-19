using Avalonia.Controls;
using Cockpit.App.Docking;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
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
    private static (AssistantIndicatorCoordinator Coordinator, CockpitViewModel Cockpit, IDockPanelRegistry Panels) _Build()
    {
        var cockpit = new CockpitViewModel();

        var settings = Substitute.For<IAssistantSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettings());

        var assistant = new AssistantSessionHost(
            cockpit,
            settings,
            Substitute.For<IAssistantProfileStore>(),
            Substitute.For<ISessionStateStore>(),
            Substitute.For<IMcpServerCatalog>(),
            Substitute.For<IAssistantMemory>(),
            Substitute.For<IAssistantTranscriptStore>(),
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

        var panels = new DockPanelRegistry();
        var coordinator = new AssistantIndicatorCoordinator(
            assistant,
            openMic,
            overlay,
            settings,
            Substitute.For<IVoicePlaybackQueue>(),
            Substitute.For<IAssistantSpawnAuditLog>(),
            cockpit,
            panels);

        coordinator.Start();
        return (coordinator, cockpit, panels);
    }

    // What the dock rail itself does with the registration: builds the panel's view. Standing in for
    // CockpitView's own _RebuildDockPanelContent, which is a view detail — the registration is the seam.
    private static Control _OpenTheRailPanel(IDockPanelRegistry panels) =>
        panels.Panels.Single(panel => panel.Id == AssistantIndicatorCoordinator.DockPanelId).CreateView();

    [Fact]
    public async Task DockingFromTheRailTab_TakesTheChatOutOfTheWindow_RatherThanLeavingTwoOnScreen()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, panels) = _Build();

            // The operator opens the chat the ordinary way: the sidebar chip, undocked, so a window.
            coordinator.Indicator.ClickCommand.Execute(null);
            await Task.Delay(100);

            var floating = coordinator.OpenChatWindow;
            Assert.NotNull(floating);

            // …then clicks the rail tab, which reaches the coordinator only through the panel factory.
            var docked = _OpenTheRailPanel(panels);
            await Task.Delay(100);

            Assert.Null(coordinator.OpenChatWindow);
            Assert.True(cockpit.AssistantDocked, "a dock the operator can see has to be the dock that is remembered");

            // One view model, two views over its life — never two conversations.
            Assert.Same(floating!.DataContext, ((AssistantChatView)docked).DataContext);
            Assert.True(((AssistantChatViewModel)docked.DataContext!).IsDocked, "the header reads this to show Undock");
        });
    }

    [Fact]
    public async Task WithTheAssistantDocked_TheChipOpensTheRail_AndNeverASecondWindow()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, _) = _Build();
            await cockpit.SetAssistantDockedAsync(true, AssistantIndicatorCoordinator.DockPanelId);

            coordinator.Indicator.ClickCommand.Execute(null);
            await Task.Delay(100);

            Assert.Null(coordinator.OpenChatWindow);
            Assert.Equal(AssistantIndicatorCoordinator.DockPanelId, cockpit.OpenDockPanelId);
        });
    }

    [Fact]
    public async Task Undocking_ClosesTheRailPanel_BeforeTheWindowTakesTheChat()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, panels) = _Build();
            await cockpit.SetAssistantDockedAsync(true, AssistantIndicatorCoordinator.DockPanelId);

            var docked = (AssistantChatView)_OpenTheRailPanel(panels);
            var chat = (AssistantChatViewModel)docked.DataContext!;
            Assert.True(chat.IsDocked);

            // The header's Undock button.
            chat.ToggleDockCommand.Execute(null);
            await Task.Delay(100);

            Assert.False(chat.IsDocked);
            Assert.False(cockpit.AssistantDocked);
            Assert.Null(cockpit.OpenDockPanelId);

            var floating = coordinator.OpenChatWindow;
            Assert.NotNull(floating);
            Assert.Same(chat, floating!.DataContext);
        });
    }

}
