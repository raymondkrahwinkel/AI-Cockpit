using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-779: <c>StartPty</c> used to call <c>ITtyLauncher.Launch</c> straight on the UI thread, which is where
/// <c>PluginTtySessionProviderAdapter</c>'s OAuth-renewal/registry calls (each up to AC-646's 5s budget) and the
/// pty spawn itself all blocked, freezing the app. The fix offloads the whole call via <c>Task.Run</c>.
/// </summary>
[Collection("avalonia")]
public class TtyStartPtyOffloadsLaunchTests
{
    [Fact]
    public Task StartPty_RunsTheLauncherOffTheUiThread() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var uiThreadId = Environment.CurrentManagedThreadId;
            var launchThreadId = new TaskCompletionSource<int>();
            var pty = Substitute.For<IConPtyProcess>();

            var launcher = Substitute.For<ITtyLauncher>();
            launcher.Launch(
                    Arg.Any<ITtySessionProvider>(), Arg.Any<SessionProfile?>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                    Arg.Any<short>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<string?>(),
                    Arg.Any<IReadOnlySet<string>?>(), Arg.Any<SessionResources?>(), Arg.Any<string?>())
                .Returns(_ =>
                {
                    launchThreadId.TrySetResult(Environment.CurrentManagedThreadId);
                    return pty;
                });

            var view = new TtyView();
            var window = new Window { Content = view, Width = 800, Height = 400 };
            window.Show();
            window.UpdateLayout();
            view.DataContext = new TtyViewModel();

            typeof(TtyView).GetField("_lastColumns", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, 80);
            typeof(TtyView).GetField("_lastRows", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, 24);

            var request = new TtyLaunchRequest(
                launcher, Substitute.For<ITtySessionProvider>(), null, new Dictionary<string, string>(), "/wd", null);
            typeof(TtyView).GetMethod("OnLaunchRequested", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(view, [request]);

            var actualLaunchThreadId = await launchThreadId.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(uiThreadId, actualLaunchThreadId);

            // The result still has to land back on the view, and thus back on the UI thread — Avalonia throws if
            // Terminal.PrepareForNewSession et al. below are touched from the wrong one.
            await _LetPostedWorkRunAsync();
            Assert.Same(pty, typeof(TtyView).GetField("_pty", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view));
        });

    // Lets StartPty's post-await continuation (queued onto the UI dispatcher once Launch returns) actually run —
    // see the identical helper in TtyPromptReadinessTests.
    private static async Task _LetPostedWorkRunAsync() => await Dispatcher.UIThread.InvokeAsync(() => { });
}
