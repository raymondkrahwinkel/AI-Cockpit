using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="AssistantChatViewModel"/> against fakes of <see cref="IAssistantSessionHost"/> — the pop-out
/// window's own peephole contract, see that interface's remarks for why it exists — rather than the real
/// <c>AssistantSessionHost</c>, which needs a live <c>CockpitViewModel</c> to construct. Covers AC-543's three
/// hard requirements for this window: criterion 7 (closing never ends the assistant's session; reopening shows
/// what stood), criterion 8 (typing needs no STT/voice dependency at all), and criterion 9 (read-aloud off
/// interrupts whatever is already playing, not just the next reply).
/// </summary>
public class AssistantChatViewModelTests
{
    private static IAssistantSessionHost FakeHost(SessionViewModel? session = null, AssistantActivity activity = AssistantActivity.Ready)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        host.Activity.Returns(activity);
        host.EnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(session));
        host.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return host;
    }

    private static IAssistantSettingsStore FakeSettingsStore(bool speakReplies = true)
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings { IsEnabled = true, SpeakReplies = speakReplies }));
        return store;
    }

    [Fact]
    public void Closing_NeverEndsTheSession_AndReopening_ShowsTheEarlierMessages()
    {
        // One host, standing across both "windows" — exactly what AssistantSessionHost is in the real app: a
        // singleton the window only ever reads from, never owns.
        var session = new SessionViewModel();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "what did AC-223 do overnight"));
        var host = FakeHost(session);
        var playback = Substitute.For<IVoicePlaybackQueue>();

        var firstWindow = new AssistantChatViewModel(host, FakeSettingsStore(), playback);
        Assert.Contains(firstWindow.Session!.Transcript, entry => entry.Text.Contains("AC-223"));

        // "Closing" a window is exactly Dispose() (AssistantChatWindow.OnClosed calls nothing else on it). There
        // is no member on IAssistantSessionHost this could even call to end a session with — Dispose only
        // detaches the peephole's own PropertyChanged subscription.
        firstWindow.Dispose();
        Assert.Same(session, host.Session);
        Assert.NotEmpty(session.Transcript);

        // Reopening — a second view model against the same still-standing host, the same as a second chip click
        // building a new window over the same AssistantSessionHost singleton — shows the earlier conversation.
        var reopened = new AssistantChatViewModel(host, FakeSettingsStore(), playback);
        Assert.Contains(reopened.Session!.Transcript, entry => entry.Text.Contains("AC-223"));
    }

    [Fact]
    public async Task Sending_NeedsNoVoiceOrSttDependency()
    {
        var host = FakeHost();
        var vm = new AssistantChatViewModel(host, FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        Assert.False(vm.SendCommand.CanExecute(null));

        // Typed text alone drives CanSend/Send — nothing here reads a microphone, a transcription service, or
        // any other voice dependency; the fakes above never see a call.
        vm.InputText = "what is the status of AC-223";
        Assert.True(vm.SendCommand.CanExecute(null));

        await vm.SendCommand.ExecuteAsync(null);

        await host.Received(1).SendAsync("what is the status of AC-223", Arg.Any<CancellationToken>());
        Assert.Equal(string.Empty, vm.InputText);
    }

    [Fact]
    public void WhitespaceInput_CannotBeSent()
    {
        var vm = new AssistantChatViewModel(FakeHost(), FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        vm.InputText = "   ";

        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task TurningReadAloudOff_InterruptsWhateverIsPlaying_AndPersists()
    {
        var settingsStore = FakeSettingsStore(speakReplies: true);
        var playback = Substitute.For<IVoicePlaybackQueue>();
        var vm = new AssistantChatViewModel(FakeHost(), settingsStore, playback);

        // Loading the current setting on open must not itself read as a click — no interrupt for a value that
        // was never touched.
        await vm.EnsureOpenedAsync();
        Assert.True(vm.SpeakReplies);
        playback.DidNotReceive().StopAll();

        vm.SpeakReplies = false;

        // The same interrupt a push-to-talk barge-in uses (criterion 9: "uitzetten midden in een zin breekt af").
        playback.Received(1).StopAll();
        await settingsStore.Received(1).SaveAsync(
            Arg.Is<AssistantSettings>(s => s.SpeakReplies == false), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpeningWithReadAloudAlreadyStoredOff_DoesNotInterruptAnything()
    {
        var playback = Substitute.For<IVoicePlaybackQueue>();
        var vm = new AssistantChatViewModel(FakeHost(), FakeSettingsStore(speakReplies: false), playback);

        await vm.EnsureOpenedAsync();

        Assert.False(vm.SpeakReplies);
        playback.DidNotReceive().StopAll();
    }

    [Fact]
    public void UnavailableHost_SurfacesTheReasonRatherThanAnEmptyWindow()
    {
        var host = FakeHost(activity: AssistantActivity.Unavailable);
        host.UnavailableReason.Returns("The assistant is switched off. Turn it on in Options → Voice.");

        var vm = new AssistantChatViewModel(host, FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        Assert.True(vm.IsUnavailable);
        Assert.Equal("The assistant is switched off. Turn it on in Options → Voice.", vm.UnavailableReason);
    }
}
