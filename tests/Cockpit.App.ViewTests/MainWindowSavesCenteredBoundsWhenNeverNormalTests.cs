using Avalonia.Threading;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-868: a window that starts maximized and stays that way for the whole session never runs
/// <c>OnResized</c> with <c>WindowState.Normal</c>, so <c>_normalPosition</c> is still the pre-Show
/// constructor guess (in practice <c>PixelPoint(0,0)</c>) — saving that verbatim is what put the window in the
/// corner on the next un-maximize. The save must fall back to a centered position instead.
/// </summary>
[Collection("avalonia")]
public class MainWindowSavesCenteredBoundsWhenNeverNormalTests
{
    [Fact]
    public Task ASessionThatStaysMaximizedTheWholeTime_SavesACenteredPosition_NotThePreShowGuess() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var store = Substitute.For<IWindowBoundsStore>();
            store.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<WindowBounds?>(null));
            WindowBounds? savedBounds = null;
            store.SaveAsync(Arg.Any<string>(), Arg.Any<WindowBounds>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    savedBounds = ci.ArgAt<WindowBounds>(1);
                    return Task.CompletedTask;
                });

            var window = new MainWindow(store);
            window.WindowState = Avalonia.Controls.WindowState.Maximized;
            window.Show();

            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            await _WaitUntilAsync(() => closed);

            Assert.NotNull(savedBounds);
            // Not the pre-Show (0,0) guess — a centered position, using the window's default XAML size.
            Assert.False(savedBounds!.X == 0 && savedBounds.Y == 0);
        });

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
        }
    }
}
