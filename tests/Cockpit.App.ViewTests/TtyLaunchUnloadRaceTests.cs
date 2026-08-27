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

[Collection("avalonia")]
public class TtyLaunchUnloadRaceTests
{
    [Fact]
    public Task ClosingThePaneWhileTheLaunchIsStillRunning_DisposesThePty() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var gate = new ManualResetEventSlim(initialState: false);
            var (view, window, pty) = _Start(gate);

            await Task.Delay(50);
            window.Content = null;
            window.UpdateLayout();
            await _LetPostedWorkRunAsync();
            pty.DidNotReceive().Dispose();

            gate.Set();
            await _LetPostedWorkRunAsync();

            pty.Received(1).Dispose();
            Assert.Null(_Field(view, "_pty"));
            Assert.Null(_Field(view, "_outputFlush"));
        });

    [Fact]
    public Task ClosingThePaneAfterTheLaunchLanded_DisposesThePty() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var gate = new ManualResetEventSlim(initialState: true);
            var (view, window, pty) = _Start(gate);

            await _LetPostedWorkRunAsync();
            Assert.Same(pty, _Field(view, "_pty"));

            window.Content = null;
            window.UpdateLayout();
            await _LetPostedWorkRunAsync();

            pty.Received(1).Dispose();
            Assert.Null(_Field(view, "_pty"));
        });

    private static object? _Field(TtyView view, string name) =>
        typeof(TtyView).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view);

    private static (TtyView View, Window Window, IConPtyProcess Pty) _Start(ManualResetEventSlim gate)
    {
        var pty = Substitute.For<IConPtyProcess>();
        var launcher = Substitute.For<ITtyLauncher>();
        launcher.Launch(
                Arg.Any<ITtySessionProvider>(), Arg.Any<SessionProfile?>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<short>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlySet<string>?>(), Arg.Any<SessionResources?>(), Arg.Any<string?>())
            .Returns(_ =>
            {
                gate.Wait(TimeSpan.FromSeconds(20));
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

        return (view, window, pty);
    }

    private static async Task _LetPostedWorkRunAsync() => await Dispatcher.UIThread.InvokeAsync(() => { });
}
