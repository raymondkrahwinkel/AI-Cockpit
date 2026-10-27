using Avalonia.Controls;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-866: the assistant pop-out now restores its own <c>"assistant"</c>-keyed bounds before <c>Show()</c>
/// (mirroring <see cref="MainWindowRestoresMaximizedStateTests"/>) and saves them again on close.
/// </summary>
[Collection("avalonia")]
public class AssistantChatWindowRestoresBoundsTests
{
    [Fact]
    public void ASavedState_RestoresPositionSizeAndMaximizedBeforeTheWindowIsShown() => HeadlessAvalonia.Run(() =>
    {
        var store = Substitute.For<IWindowBoundsStore>();
        store.LoadAsync("assistant", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<WindowBounds?>(new WindowBounds(50, 50, 800, 600, IsMaximized: false)),
                Task.FromResult<WindowBounds?>(new WindowBounds(50, 50, 800, 600, IsMaximized: true)));

        var window = new AssistantChatWindow(store);

        Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
        Assert.Equal(new Avalonia.PixelPoint(50, 50), window.Position);
        Assert.Equal(800, window.Width);
        Assert.Equal(600, window.Height);
        Assert.Equal(WindowState.Maximized, new AssistantChatWindow(store).WindowState);
    });

    [Fact]
    public void NoSavedBounds_KeepsTheXamlDefaultCenterOwnerLocation_AndClosingSavesUnderTheAssistantKey() => HeadlessAvalonia.Run(() =>
    {
        var store = Substitute.For<IWindowBoundsStore>();
        store.LoadAsync("assistant", Arg.Any<CancellationToken>()).Returns(Task.FromResult<WindowBounds?>(null));
        store.SaveAsync("assistant", Arg.Any<WindowBounds>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        Assert.Equal(WindowStartupLocation.CenterOwner, new AssistantChatWindow(store).WindowStartupLocation);

        var window = new AssistantChatWindow(store) { Position = new Avalonia.PixelPoint(10, 20), Width = 500, Height = 700 };
        window.Show();
        window.Close();

        _ = store.Received(1).SaveAsync("assistant", Arg.Any<WindowBounds>(), Arg.Any<CancellationToken>());
    });
}
