using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What a message may carry in the assistant's chat window (AC-630): an image with no words, and the queued
/// message Arrow-Up pulls back to edit.
/// </summary>
/// <remarks>
/// Here rather than in the unit tests because queuing an attachment decodes a preview bitmap, which Avalonia
/// cannot do without a platform.
/// </remarks>
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
        var session = new SessionViewModel();
        var host = _FakeHost(session);
        var vm = new AssistantChatViewModel(host, _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());
        session.PendingAttachments.Add(new ImageAttachmentViewModel(Png, a => session.PendingAttachments.Remove(a)));

        Assert.True(vm.SendCommand.CanExecute(null));

        await vm.SendCommand.ExecuteAsync(null);

        await host.Received().SendAsync(string.Empty, Arg.Any<CancellationToken>());
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
        session.QueuedMessages.Add(new QueuedMessageViewModel("kijk hier nog eens naar", [], replyTo: null, m => session.QueuedMessages.Remove(m)));

        Assert.True(vm.RecallLastQueuedMessage());

        Assert.Equal("kijk hier nog eens naar", vm.InputText);
        Assert.Empty(session.InputText);
        Assert.Empty(session.QueuedMessages);
    });

    /// <summary>An empty queue leaves Arrow-Up free to move the caret as usual.</summary>
    [Fact]
    public void RecallingWithAnEmptyQueue_DoesNothing() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.QueuedMessages.Clear();
        var vm = new AssistantChatViewModel(_FakeHost(session), _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        Assert.False(vm.RecallLastQueuedMessage());
        Assert.Empty(vm.InputText);
    });

    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));

    /// <summary>AC-942 criterion 6: no session yet, no button.</summary>
    [Fact]
    public void StopButton_HiddenWithNoSession() => HeadlessAvalonia.Run(() =>
    {
        using var pane = _Pane(_FakeHost(session: null));

        Assert.False(_StopButton(pane.Window).IsEffectivelyVisible);
    });

    /// <summary>AC-942 criterion 1/6: hidden while idle, shown while a turn is running.</summary>
    [Fact]
    public void StopButton_TracksSessionIsBusy() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        using var pane = _Pane(_FakeHost(session));
        Assert.False(_StopButton(pane.Window).IsEffectivelyVisible);

        session.IsBusy = true;
        pane.Window.UpdateLayout();

        Assert.True(_StopButton(pane.Window).IsEffectivelyVisible);
    });

    /// <summary>AC-942 criteria 2 and 4: clicking Stop interrupts the running turn and cuts read-aloud.</summary>
    [Fact]
    public async Task ClickingStop_InterruptsTheTurn_AndStopsReadAloud() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var (vm, _, driver, playback) = await _StartedVmAsync();

        await vm.StopCommand.ExecuteAsync(null);

        await driver.Received().InterruptAsync(Arg.Any<CancellationToken>());
        playback.Received().StopAll();
    });

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

    private sealed record Pane(AssistantChatWindow Window, AssistantChatViewModel ViewModel) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    private static Pane _Pane(IAssistantSessionHost host)
    {
        var vm = new AssistantChatViewModel(host, _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());
        var window = new AssistantChatWindow { Width = 420, Height = 560, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return new Pane(window, vm);
    }

    private static Button _StopButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StopButton");

    private static TextBox _InputBox(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "InputBox");

    private static async Task<(AssistantChatViewModel Vm, SessionViewModel Session, ISessionDriver Driver, IVoicePlaybackQueue Playback)> _StartedVmAsync()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyEvents());
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var session = new SessionViewModel(new SessionManager(factory));
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

    // AC-935 criterion 2: a row's reply button sets the composer's pending target, which the chip shows and its
    // own cancel clears — same session state the send path reads, not a separate view-only flag.
    [Fact]
    public void SettingAReplyTarget_ShowsTheChip_AndCancellingItHidesItAgain() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        var target = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "please check the build output");
        session.Transcript.Add(target);

        var window = new AssistantChatWindow
        {
            Width = 420,
            Height = 560,
            DataContext = new AssistantChatViewModel(_FakeHost(session), _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>()),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.False(window.ChatView.ReplyChip.IsVisible);

            session.SetReplyTargetCommand.Execute(target);
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.ChatView.ReplyChip.IsVisible);

            window.ChatView.ReplyChipCancelButton.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.ChatView.ReplyChip.IsVisible);
            Assert.Null(session.PendingReplyTo);
        }
        finally
        {
            window.Close();
        }
    });

    // Bug found while verifying AC-935 (pre-existing, not caused by it): `CanSend` never raised
    // PropertyChanged, so `SendButton.IsEnabled="{Binding CanSend}"` stayed at whatever it read on the
    // window's first render — grey forever, typed text or not. Unnoticed because Enter bypasses the
    // button and checks CanExecute directly (AssistantChatWindow._OnInputKeyDownCore).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypingIntoTheBox_EnablesSend_RegardlessOfWhetherATurnIsInFlight(bool busy) => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        session.IsBusy = busy;

        var window = new AssistantChatWindow
        {
            Width = 420,
            Height = 560,
            DataContext = new AssistantChatViewModel(_FakeHost(session), _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>()),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.False(window.ChatView.SendButton.IsEnabled);

            ((AssistantChatViewModel)window.DataContext!).InputText = "looks fine to me";
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.ChatView.SendButton.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    // The other half of CanSend's dependencies — an attachment with no typed text must re-enable Send the
    // same way typing does (same bug, same fix: PendingAttachments.CollectionChanged now re-raises CanSend).
    [Fact]
    public void AddingAPendingAttachment_EnablesSend() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();

        var window = new AssistantChatWindow
        {
            Width = 420,
            Height = 560,
            DataContext = new AssistantChatViewModel(_FakeHost(session), _FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>()),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.False(window.ChatView.SendButton.IsEnabled);

            session.PendingAttachments.Add(new ImageAttachmentViewModel(Png, a => session.PendingAttachments.Remove(a)));
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.ChatView.SendButton.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    private static IAssistantSessionHost _FakeHost(SessionViewModel? session)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        host.Activity.Returns(AssistantActivity.Ready);
        host.EnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<SessionViewModel?>(session));
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
