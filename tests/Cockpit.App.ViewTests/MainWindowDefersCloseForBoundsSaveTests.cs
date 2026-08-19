using System.Reflection;
using Avalonia.Threading;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-779: <c>MainWindow.OnClosing</c> used to block the UI thread with
/// <c>_windowBoundsStore.SaveAsync(...).GetAwaiter().GetResult()</c>. It now cancels the close, awaits the save,
/// and replays <c>Close()</c> once it finishes — so a slow write no longer freezes the window on shutdown.
/// </summary>
[Collection("avalonia")]
public class MainWindowDefersCloseForBoundsSaveTests
{
    [Fact]
    public Task OnClosing_DefersTheRealCloseUntilTheSaveCompletes_WithoutBlockingTheUiThread() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var store = Substitute.For<IWindowBoundsStore>();
            store.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<WindowBounds?>(null));
            var saveGate = new TaskCompletionSource();
            store.SaveAsync(Arg.Any<string>(), Arg.Any<WindowBounds>(), Arg.Any<CancellationToken>()).Returns(saveGate.Task);

            var window = new MainWindow();
            typeof(MainWindow).GetField("_windowBoundsStore", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(window, store);
            window.Show();

            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Close();

            // Still pending: the close must not have gone through yet, and — the point of this fix — the UI
            // thread must still be free to process other dispatcher work while it waits (the old
            // GetAwaiter().GetResult() would have frozen right here instead of letting this round-trip complete).
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.False(closed);

            saveGate.SetResult();
            await Dispatcher.UIThread.InvokeAsync(() => { });
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.True(closed);
            _ = store.Received(1).SaveAsync(Arg.Any<string>(), Arg.Any<WindowBounds>(), Arg.Any<CancellationToken>());
        });

    [Fact]
    public Task OnClosing_ASaveThatThrows_StillLetsTheWindowClose() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var store = Substitute.For<IWindowBoundsStore>();
            store.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<WindowBounds?>(null));
            store.SaveAsync(Arg.Any<string>(), Arg.Any<WindowBounds>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new IOException("disk is unhappy")));

            var window = new MainWindow();
            typeof(MainWindow).GetField("_windowBoundsStore", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(window, store);
            window.Show();

            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Close();
            await _WaitUntilAsync(() => closed);

            Assert.True(closed);
        });

    // A faulted Task's continuation still needs the dispatcher pumped, and exactly how many round trips that takes
    // depends on the async chain above — polling with a bound is simpler than counting hops.
    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
        }
    }
}
