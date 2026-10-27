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
    public void ASavedState_MaximizedOrNormal_IsAppliedBeforeTheWindowIsShown() => HeadlessAvalonia.Run(() =>
    {
        // No Show() call: the point of the fix is that the WM never sees this window mapped in any state but
        // Maximized, so the property must already read Maximized the moment the constructor returns.
        var maximized = new MainWindow(_Store(new WindowBounds(50, 50, 800, 600, IsMaximized: true)));

        Assert.Equal(WindowState.Maximized, maximized.WindowState);

        var normal = new MainWindow(_Store(new WindowBounds(50, 50, 800, 600, IsMaximized: false)));

        Assert.Equal(WindowState.Normal, normal.WindowState);
        Assert.Equal(new Avalonia.PixelPoint(50, 50), normal.Position);
        Assert.Equal(800, normal.Width);
        Assert.Equal(600, normal.Height);
    });

    private static IWindowBoundsStore _Store(WindowBounds bounds)
    {
        var store = Substitute.For<IWindowBoundsStore>();
        store.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<WindowBounds?>(bounds));
        return store;
    }
}
