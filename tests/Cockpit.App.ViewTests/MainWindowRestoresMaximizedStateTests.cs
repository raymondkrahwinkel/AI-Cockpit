using Avalonia.Controls;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-801: restoring a saved maximized state used to happen in <c>OnOpened</c>, after the window was already
/// shown — on X11 that races the WM applying the state to an already-mapped window.
/// </summary>
[Collection("avalonia")]
public class MainWindowRestoresMaximizedStateTests
{
    [Fact]
    public void ASavedMaximizedState_IsAppliedBeforeTheWindowIsShown() => HeadlessAvalonia.Run(() =>
    {
        var store = Substitute.For<IWindowBoundsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WindowBounds?>(new WindowBounds(50, 50, 800, 600, IsMaximized: true)));

        // No Show() call: the point of the fix is that the WM never sees this window mapped in any state but
        // Maximized, so the property must already read Maximized the moment the constructor returns.
        var window = new MainWindow(store);

        Assert.Equal(WindowState.Maximized, window.WindowState);
    });

    [Fact]
    public void ASavedNormalState_RestoresPositionAndSizeBeforeTheWindowIsShown() => HeadlessAvalonia.Run(() =>
    {
        var store = Substitute.For<IWindowBoundsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<WindowBounds?>(new WindowBounds(50, 50, 800, 600, IsMaximized: false)));

        var window = new MainWindow(store);

        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(new Avalonia.PixelPoint(50, 50), window.Position);
        Assert.Equal(800, window.Width);
        Assert.Equal(600, window.Height);
    });
}
