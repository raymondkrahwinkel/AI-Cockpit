using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-962's drag-to-dock gesture, driven the way an operator drives it: press the chat window's header, move, let
/// go. The managed move exists so this is answerable at all — an OS move loop reports neither the pointer nor the
/// release, which is why the gesture could not be built on <c>BeginMoveDrag</c>.
/// </summary>
[Collection("avalonia")]
public sealed class AssistantDragToDockTests
{
    // The headless screen is 1920×1280 at scaling 1, so a cockpit window of that size covers the drop-zone band
    // along its right edge — which is the condition the zone exists under, not a convenience.
    private const int ScreenWidth = 1920;
    private const int ScreenHeight = 1280;

    // Inside the right-hand 20% band (from x = 1536) and outside it, both in screen coordinates. Headless maps a
    // client point to a screen point by scaling alone, ignoring the window's position — so these are the points a
    // move is sent to, and where the window lands as a result is a number only this backend produces.
    private static readonly Point InsideTheZone = new(1700, 14);
    private static readonly Point OutsideTheZone = new(700, 14);

    private static Window _ShowCockpit(CockpitViewModel cockpit)
    {
        var main = new Window
        {
            Width = ScreenWidth,
            Height = ScreenHeight,
            DataContext = cockpit,
            Content = new CockpitView(),
        };

        main.Show();
        main.UpdateLayout();
        return main;
    }

    // A point on the header that is not one of its buttons — the avatar and title sit left of them, and the
    // gesture ignores a press that starts on a button. Found by name over the whole tree: the header lives in
    // AssistantChatView, which has a name scope of its own.
    private static Point _HeaderGrip(Window chatWindow)
    {
        var header = chatWindow.GetVisualDescendants().OfType<Border>().First(border => border.Name == "HeaderBar");
        return header.TranslatePoint(new Point(60, header.Bounds.Height / 2), chatWindow)!.Value;
    }

    private static async Task<(AssistantIndicatorCoordinator Coordinator, CockpitViewModel Cockpit, Window Main, AssistantChatWindow Chat)> _OpenTheFloatingChatAsync()
    {
        var (coordinator, cockpit, _) = AssistantDockHostSwapTests.Build();
        var main = _ShowCockpit(cockpit);

        coordinator.Indicator.ClickCommand.Execute(null);
        await Task.Delay(100);

        var chat = coordinator.OpenChatWindow!;

        // What the coordinator hands over when there is an application lifetime to read a main window off; the
        // headless harness has none, so the test plays that part.
        chat.CockpitWindow = main;
        chat.Show();
        chat.UpdateLayout();

        return (coordinator, cockpit, main, chat);
    }

    [Fact]
    public async Task ReleasingInsideTheZone_DocksTheAssistant_AndTakesTheWindowAway()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, main, chat) = await _OpenTheFloatingChatAsync();

            try
            {
                var grip = _HeaderGrip(chat);

                chat.MouseDown(grip, MouseButton.Left);
                Assert.True(cockpit.IsAssistantDropZoneVisible, "the zone shows for as long as the drag runs");
                Assert.False(cockpit.IsAssistantDropZoneActive, "the pointer starts outside it");

                chat.MouseMove(InsideTheZone, RawInputModifiers.LeftMouseButton);
                Assert.True(cockpit.IsAssistantDropZoneActive, "the zone lights up while the pointer is inside");

                chat.MouseUp(InsideTheZone, MouseButton.Left);
                await Task.Delay(100);

                Assert.True(cockpit.AssistantDocked);
                Assert.Null(coordinator.OpenChatWindow);
                Assert.False(cockpit.IsAssistantDropZoneVisible, "nothing of the zone is left once the drag ends");
            }
            finally
            {
                main.Close();
            }
        });
    }

    [Fact]
    public async Task ReleasingOutsideTheZone_LeavesTheWindowWhereItWasDropped_StillUndocked()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, main, chat) = await _OpenTheFloatingChatAsync();

            try
            {
                var start = chat.Position;
                var grip = _HeaderGrip(chat);

                chat.MouseDown(grip, MouseButton.Left);
                chat.MouseMove(OutsideTheZone, RawInputModifiers.LeftMouseButton);
                Assert.False(cockpit.IsAssistantDropZoneActive);

                chat.MouseUp(OutsideTheZone, MouseButton.Left);
                await Task.Delay(100);

                Assert.False(cockpit.AssistantDocked);
                Assert.Same(chat, coordinator.OpenChatWindow);
                Assert.NotEqual(start, chat.Position);
                Assert.False(cockpit.IsAssistantDropZoneVisible);
            }
            finally
            {
                main.Close();
            }
        });
    }

    [Fact]
    public async Task EscapeMidDrag_PutsTheWindowBackWhereItWasPickedUp_AndDocksNothing()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, main, chat) = await _OpenTheFloatingChatAsync();

            try
            {
                var start = chat.Position;
                var grip = _HeaderGrip(chat);

                chat.MouseDown(grip, MouseButton.Left);
                chat.MouseMove(InsideTheZone, RawInputModifiers.LeftMouseButton);
                Assert.NotEqual(start, chat.Position);

                chat.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                await Task.Delay(100);

                Assert.Equal(start, chat.Position);
                Assert.False(cockpit.AssistantDocked);
                Assert.Same(chat, coordinator.OpenChatWindow);
                Assert.False(cockpit.IsAssistantDropZoneVisible);

                // And the release that follows the operator letting go of the button docks nothing either: Escape
                // ended the drag, so what is left is an ordinary click on the header.
                chat.MouseUp(InsideTheZone, MouseButton.Left);
                await Task.Delay(100);

                Assert.False(cockpit.AssistantDocked);
                Assert.Equal(start, chat.Position);
            }
            finally
            {
                main.Close();
            }
        });
    }

    // The zone is the overlap between the screen band and the cockpit window, so a cockpit that does not reach
    // the band offers nothing to drop on — and a drop there must not dock behind the operator's back.
    [Fact]
    public async Task WithTheCockpitAwayFromTheBand_ThereIsNoZone_AndAReleaseDocksNothing()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var (coordinator, cockpit, _) = AssistantDockHostSwapTests.Build();
            var main = new Window { Width = 800, Height = 600, DataContext = cockpit, Content = new CockpitView() };
            main.Show();
            main.UpdateLayout();

            coordinator.Indicator.ClickCommand.Execute(null);
            await Task.Delay(100);

            var chat = coordinator.OpenChatWindow!;
            chat.CockpitWindow = main;
            chat.Show();
            chat.UpdateLayout();

            try
            {
                var grip = _HeaderGrip(chat);

                chat.MouseDown(grip, MouseButton.Left);
                Assert.False(cockpit.IsAssistantDropZoneVisible);

                chat.MouseMove(InsideTheZone, RawInputModifiers.LeftMouseButton);
                chat.MouseUp(InsideTheZone, MouseButton.Left);
                await Task.Delay(100);

                Assert.False(cockpit.AssistantDocked);
                Assert.Same(chat, coordinator.OpenChatWindow);
            }
            finally
            {
                main.Close();
            }
        });
    }
}
