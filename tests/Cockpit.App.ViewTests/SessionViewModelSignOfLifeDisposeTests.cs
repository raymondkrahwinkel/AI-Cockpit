using System.Reflection;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Assistant;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-786: the sign-of-life timer (voice-assistant pane only, AC-598) used to outlive dispose when a turn was
/// still running — its own guard (<c>!IsBusy</c>) only stops it on its next tick, which never comes once the
/// pane is torn down, so it kept the whole <see cref="SessionViewModel"/> alive forever.
/// </summary>
[Collection("avalonia")]
public class SessionViewModelSignOfLifeDisposeTests
{
    [Fact]
    public async Task Dispose_WhileATurnIsRunning_StopsTheSignOfLifeTimer() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var vm = new SessionViewModel();
        vm.AdoptPaneId(AssistantIdentity.PaneId);
        vm.ReadResponsesAloud = true;
        vm.IsBusy = true;

        typeof(SessionViewModel)
            .GetMethod("_RestartSignOfLifeClock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, null);
        var timer = (DispatcherTimer)typeof(SessionViewModel)
            .GetField("_signOfLifeTimer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(vm)!;
        Assert.True(timer.IsEnabled);

        await vm.DisposeAsync();

        Assert.False(timer.IsEnabled);
    });
}
