using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using NSubstitute;
using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-946: an accidental click on the close button used to end the app — and every running session — with no
/// warning. <c>MainWindow.OnClosing</c> now asks for confirmation first, but only when there is something to
/// lose and only for an operator-initiated close (a programmatic quit skips straight through, see
/// <see cref="Cockpit.App.App.IsQuitting"/>).
/// </summary>
[Collection("avalonia")]
public class MainWindowCloseConfirmationTests
{
    [Fact]
    public Task OnClosing_WithActiveSessions_AsksAndStaysOpenWhenCancelled() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var dialogService = Substitute.For<ISessionDialogService>();
            dialogService.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(false));

            var window = new MainWindow { DataContext = NewVm(dialogService) };
            window.Show();

            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Close();
            await _PumpAsync();

            Assert.False(closed);
            await dialogService.Received(1).ShowConfirmationDialogAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        });

    [Fact]
    public Task OnClosing_WithActiveSessions_ClosesOnceConfirmed() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var dialogService = Substitute.For<ISessionDialogService>();
            dialogService.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromResult(true));

            var window = new MainWindow { DataContext = NewVm(dialogService) };
            window.Show();

            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Close();
            await _WaitUntilAsync(() => closed);

            Assert.True(closed);
        });

    [Fact]
    public Task OnClosing_WithNoActiveSessions_ClosesWithoutAsking() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var dialogService = Substitute.For<ISessionDialogService>();

            var vm = NewVm(dialogService);
            vm.Sessions.Clear();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.Close();
            await _WaitUntilAsync(() => closed);

            Assert.True(closed);
            _ = dialogService.DidNotReceive().ShowConfirmationDialogAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
            await _UnsavedFileCloseAsync();
        });

    private static async Task _UnsavedFileCloseAsync()
    {
        var accepted = false;
        var confirmations = 0;
        var registry = new WorkspaceTypeRegistry(new ServiceCollection().BuildServiceProvider());
        registry.Register(new WorkspaceTypeRegistration("file.close", "File", context =>
        {
            context.SetCloseGuard(() =>
            {
                confirmations++;
                return Task.FromResult(accepted);
            });
            return new TextBlock { Text = "Unsaved text" };
        }), new PluginScenes.SceneStorage(), NullCockpitSessionObserver.Instance);
        var vm = new CockpitViewModel(workspaceTypeRegistry: registry);
        vm.Sessions.Clear();
        await vm.Workspaces.OpenWorkspaceAsync("file.close");
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Assert.False(await window.ConfirmWorkspaceCloseAsync());
        Assert.True(window.IsVisible);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        await _PumpAsync();
        Assert.True(window.IsVisible);
        accepted = true;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        await _WaitUntilAsync(() => !window.IsVisible);
        Assert.False(window.IsVisible);
        Assert.Equal(3, confirmations);
        await _NewDraftDuringCloseAsync();
        await _RestartBeforeDiscardAsync();
    }

    private static async Task _NewDraftDuringCloseAsync()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answers = new Dictionary<string, Task<bool>> { ["first"] = ready.Task, ["late"] = Task.FromResult(false) };
        var registry = new WorkspaceTypeRegistry(new ServiceCollection().BuildServiceProvider());
        registry.Register(new WorkspaceTypeRegistration("file.race", "File", context =>
        {
            context.SetCloseGuard(() => answers[context.InstanceKey ?? "first"]);
            return new TextBlock();
        }), new PluginScenes.SceneStorage(), NullCockpitSessionObserver.Instance);
        var vm = new WorkspacesViewModel(store: null, workspaceTypes: registry);
        await vm.OpenWorkspaceAsync("file.race", new WorkspaceOpenOptions("first"));
        var closing = vm.ConfirmCloseAllAsync();
        await vm.OpenWorkspaceAsync("file.race", new WorkspaceOpenOptions("late"));
        ready.SetResult(true);
        Assert.False(await closing);
    }

    private static async Task _RestartBeforeDiscardAsync()
    {
        var approved = false;
        var updaterStarts = 0;
        var launches = 0;
        var shutdowns = 0;
        var restart = new AppRestartService(() => { updaterStarts++; return true; }, () => launches++, () => shutdowns++,
            confirmWorkspaceClose: () => Task.FromResult(approved));
        await restart.RestartAsync();
        Assert.Equal((0, 0, 0), (updaterStarts, launches, shutdowns));
        approved = true;
        await restart.RestartAsync();
        Assert.Equal((1, 0, 1), (updaterStarts, launches, shutdowns));
    }

    private static async Task _PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { });
        await Dispatcher.UIThread.InvokeAsync(() => { });
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
        }
    }

    private static CockpitViewModel NewVm(ISessionDialogService dialogService)
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        var vm = new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogService,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore);

        vm.Sessions.Add(new SessionViewModel { Title = "Session 1" });
        return vm;
    }
}
