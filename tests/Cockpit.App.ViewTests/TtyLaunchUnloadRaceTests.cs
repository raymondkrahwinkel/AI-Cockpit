using System.Reflection;
using Avalonia.Controls;
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
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (view, window, pty, launchStarted, _, ptyDisposed) = _Start(gate.Task);

            await launchStarted.WaitAsync(TimeSpan.FromSeconds(5));
            window.Content = null;
            window.UpdateLayout();
            Assert.Null(view.Parent);
            pty.DidNotReceive().Dispose();

            gate.SetResult();
            await ptyDisposed.WaitAsync(TimeSpan.FromSeconds(5));

            pty.Received(1).Dispose();
            Assert.Null(_Field(view, "_pty"));
            Assert.Null(_Field(view, "_outputFlush"));
        });

    [Fact]
    public Task ClosingThePaneAfterTheLaunchLanded_DisposesThePty() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var gate = Task.CompletedTask;
            var (view, window, pty, _, ptyLanded, ptyDisposed) = _Start(gate);

            await ptyLanded.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(pty, _Field(view, "_pty"));

            window.Content = null;
            window.UpdateLayout();
            Assert.Null(view.Parent);
            await ptyDisposed.WaitAsync(TimeSpan.FromSeconds(5));

            pty.Received(1).Dispose();
            Assert.Null(_Field(view, "_pty"));
        });

    private static object? _Field(TtyView view, string name) =>
        typeof(TtyView).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view);

    private static (TtyView View, Window Window, IConPtyProcess Pty, Task LaunchStarted, Task PtyLanded, Task PtyDisposed)
        _Start(Task gate)
    {
        var launchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ptyLanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ptyDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pty = Substitute.For<IConPtyProcess>();
        pty.ProcessId.Returns(_ =>
        {
            // StartPty reads ProcessId only after assigning _pty, so this marks the landed launch.
            ptyLanded.TrySetResult();
            return 1;
        });
        pty.When(pty => pty.Dispose()).Do(_ => ptyDisposed.TrySetResult());
        var launcher = Substitute.For<ITtyLauncher>();
        launcher.Launch(
                Arg.Any<ITtySessionProvider>(), Arg.Any<SessionProfile?>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<short>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlySet<string>?>(), Arg.Any<SessionResources?>(), Arg.Any<string?>())
            .Returns(_ =>
            {
                launchStarted.TrySetResult();
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

        return (view, window, pty, launchStarted.Task, ptyLanded.Task, ptyDisposed.Task);
    }
}
