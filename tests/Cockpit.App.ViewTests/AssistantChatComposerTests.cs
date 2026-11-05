using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.ModelIntelligence;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.ModelIntelligence;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Tests.Shared;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// Attachment previews need Avalonia's bitmap decoder.
[Collection("avalonia")]
public class AssistantChatComposerTests
{
    /// <summary>A real 1×1 PNG — the attachment chip decodes what it is given.</summary>
    private static byte[] Png => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>
    /// Criterion 4: a pasted image with nothing typed goes, instead of hanging in the strip forever. Both gates
    /// used to refuse it — the window's CanSend and the host's own empty-text guard.
    /// </summary>
    [Fact]
    public async Task AMessageWithOnlyAnImage_Sends() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var events = Channel.CreateUnbounded<SessionEvent>();
        var driver = Substitute.For<ISessionDriver>();
        driver.Capabilities.Returns(SessionCapabilities.ClaudeCli);
        driver.Events.Returns(events.Reader.ReadAllAsync());
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var providers = Substitute.For<IProviderModelIntelligence>();
        providers.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new PluginModelIntelligence
        {
            Models = [new("opus") { PublicModelRef = "~anthropic/claude-opus-latest" }],
        });
        var catalog = Substitute.For<IModelIntelligence>();
        var snapshots = new TaskCompletionSource<IReadOnlyList<ModelSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.GetSnapshotsAsync(Arg.Any<CancellationToken>()).Returns(snapshots.Task);
        var reader = new ModelImageInputReader(catalog, providers, Substitute.For<IPluginProviderRegistry>());
        var now = DateTimeOffset.UtcNow;
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(_ => now);
        var scheduled = new TaskCompletionSource<TimerCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>()).Returns(call =>
        {
            scheduled.TrySetResult(call.ArgAt<TimerCallback>(0));
            return Substitute.For<ITimer>();
        });
        var profiles = Substitute.For<ISessionProfileStore>();
        var runtime = new SessionRuntime(factory, Profile, imageInput: reader, profiles: profiles, timeProvider: clock);
        var manager = Substitute.For<ISessionManager>();
        manager.Create(Arg.Any<SessionProfile?>()).Returns(runtime);
        await using var session = TestSessions.Pane(manager);
        await session.StartConfiguredAsync(Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.ResolveModel("opus"), SessionOptionCatalog.DefaultEffort);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.EventAppended += evt =>
        {
            if (evt is SessionCapabilitiesChanged { Capabilities.SupportsVision: true })
            {
                ready.TrySetResult();
            }
            if (evt is SessionInitialized)
            {
                confirmed.TrySetResult(runtime.Capabilities?.SupportsVision == true);
            }
        };
        var snapshot = new ModelSnapshot("~anthropic/claude-opus-latest", "Opus", new(null, null), null, null, new(null, null, null), [], [],
            "OpenRouter", now, TimeSpan.FromHours(1)) { SupportsImageInput = true, AliasTarget = "anthropic/claude-opus-4.7" };
        snapshots.SetResult([snapshot]);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await runtime.SendUserMessageAsync("hello");
        events.Writer.TryWrite(new SessionInitialized { SessionId = "S1", Cwd = "/repo", Tools = [], Model = "claude-opus-4-7" });
        Assert.True(await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var refresh = await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await reader.ReadAsync(Profile, "claude-opus-4-7")).OpenRouter?.SupportsImageInput);
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.CanPasteImages);
        var host = _FakeHost(session);
        var vm = new AssistantChatViewModel(host, _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());
        session.PendingAttachments.Add(new ImageAttachmentViewModel(Png, a => session.PendingAttachments.Remove(a)));

        Assert.True(vm.SendCommand.CanExecute(null));

        await vm.SendCommand.ExecuteAsync(null);

        await host.Received().SendAsync(string.Empty, Arg.Any<CancellationToken>());

        clock.Received().CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan);
        catalog.GetSnapshotsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<ModelSnapshot>>(new HttpRequestException("offline")));
        var retry = await RefreshAsync(refresh, TimeSpan.FromHours(1), TimeSpan.FromSeconds(30));
        Assert.True(runtime.Capabilities?.SupportsVision);
        retry = await RefreshAsync(retry, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        retry = await RefreshAsync(retry, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120));
        retry = await RefreshAsync(retry, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(240));
        retry = await RefreshAsync(retry, TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(480));
        retry = await RefreshAsync(retry, TimeSpan.FromSeconds(480), TimeSpan.FromMinutes(15));
        retry = await RefreshAsync(retry, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

        catalog.GetSnapshotsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ModelSnapshot>>([snapshot with { SupportsImageInput = false, FetchedAt = now }]));
        retry = await RefreshAsync(retry, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(45));
        Assert.False(runtime.Capabilities?.SupportsVision);
        catalog.GetSnapshotsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<ModelSnapshot>>(new HttpRequestException("offline")));
        retry = await RefreshAsync(retry, TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(30));

        clock.ClearReceivedCalls();
        catalog.ClearReceivedCalls();
        profiles.Saved += Raise.Event<Func<IReadOnlyList<SessionProfile>, Task>>((object)new[] { Profile with { ImageInputMode = ImageInputMode.Off } });
        retry(null);
        profiles.Saved += Raise.Event<Func<IReadOnlyList<SessionProfile>, Task>>((object)new[] { Profile with { ImageInputMode = ImageInputMode.On } });
        retry(null);
        clock.DidNotReceive().CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>());
        await catalog.DidNotReceive().GetSnapshotsAsync(Arg.Any<CancellationToken>());

        async Task<TimerCallback> RefreshAsync(TimerCallback callback, TimeSpan elapsed, TimeSpan expected)
        {
            now += elapsed;
            scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            clock.ClearReceivedCalls();
            callback(null);
            var next = await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Received(1).CreateTimer(Arg.Any<TimerCallback>(), Arg.Any<object?>(), expected, Timeout.InfiniteTimeSpan);
            return next;
        }
    });

    /// <summary>
    /// Criterion 5: Arrow-Up on an empty box brings the last queued message back to edit. The session's own recall
    /// puts it in the session's composer, which this window does not show — so it has to land in the window's box.
    /// </summary>
    [Fact]
    public void RecallingAQueuedMessage_PutsItInTheWindowsOwnInputBox() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        // The parameterless constructor is the previewer's — it seeds a sample queued message; a real session
        // starts with an empty queue.
        session.QueuedMessages.Clear();
        var vm = new AssistantChatViewModel(_FakeHost(session), _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());
        session.QueuedMessages.Add(new QueuedMessageViewModel(new QueuedPrompt("kijk hier nog eens naar", []), replyTo: null, m => session.QueuedMessages.Remove(m)));

        Assert.True(vm.RecallLastQueuedMessage());

        Assert.Equal("kijk hier nog eens naar", vm.InputText);
        Assert.Empty(session.InputText);
        Assert.Empty(session.QueuedMessages);
    });

    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    /// <summary>AC-942 criterion 3: Esc interrupts the turn, same as clicking Stop.</summary>
    [Fact]
    public async Task Escape_WhileBusy_InterruptsTheTurn() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var (vm, session, driver, playback) = await _StartedVmAsync();
        session.IsBusy = true;
        var window = new AssistantChatWindow { Width = 420, Height = 560, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var box = _InputBox(window);

        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();

        await driver.Received().InterruptAsync(Arg.Any<CancellationToken>());
        playback.Received().StopAll();
        window.Close();
    });

    /// <summary>AC-942 criterion 3: an open mention picker wins Esc over the interrupt.</summary>
    [Fact]
    public async Task Escape_WithMentionPickerOpen_ClosesThePicker_AndDoesNotInterrupt() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var (vm, session, driver, _) = await _StartedVmAsync();
        session.IsBusy = true;
        session.WorkingDirectory = "/repo";
        var window = new AssistantChatWindow { Width = 420, Height = 560, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var box = _InputBox(window);
        box.Text = "@";
        box.CaretIndex = 1;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.None });
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.MentionPicker.IsOpen);

        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.MentionPicker.IsOpen);
        await driver.DidNotReceive().InterruptAsync(Arg.Any<CancellationToken>());
        window.Close();
    });

    private static TextBox _InputBox(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "InputBox");

    private static async Task<(AssistantChatViewModel Vm, SessionViewModel Session, ISessionDriver Driver, IVoicePlaybackQueue Playback)> _StartedVmAsync()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyEvents());
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var session = TestSessions.Pane(new SessionManager(factory));
        await session.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        var playback = Substitute.For<IVoicePlaybackQueue>();
        var vm = new AssistantChatViewModel(_FakeHost(session), _FakeSettingsStore(), playback);
        return (vm, session, driver, playback);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyEvents(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    private static IAssistantSessionHost _FakeHost(SessionViewModel? session)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(TestSessions.Assistant(session));
        host.Activity.Returns(AssistantActivity.Ready);
        host.EnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IAssistantSession?>(TestSessions.Assistant(session)));
        host.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return host;
    }

    private static IAssistantSettingsStore _FakeSettingsStore()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        return store;
    }
}
