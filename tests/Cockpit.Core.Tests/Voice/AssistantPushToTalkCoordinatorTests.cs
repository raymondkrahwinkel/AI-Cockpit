using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Tests.Hotkeys;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="AssistantPushToTalkCoordinator"/>'s hold logic — the twin of
/// <see cref="VoicePushToTalkCoordinatorTests"/>, driving the same internal UI-thread seam for the same reason.
/// </summary>
public class AssistantPushToTalkCoordinatorTests
{
    [Fact]
    public async Task AHoldThatTranscribedNothing_SaysSo_RatherThanEndingInSilence()
    {
        // The most common way speaking to the assistant fails, and the only one that used to say nothing at all: a
        // hold shorter than a second or two gives the voice-activity detector too little to find speech in, so the
        // capture is discarded and the transcript comes back empty — correctly. What was missing is anyone telling
        // the operator. The chip flicked back to Ready, no words appeared, and unlike the dictation path there is
        // no composer here to show an empty result. Held against a live microphone, that is indistinguishable from
        // an assistant that ignored you, and it costs an attempt every single time.
        var (coordinator, overlay, assistant, pushToTalk, _, _) = _Coordinator();
        pushToTalk.EndHoldAsync().Returns(string.Empty);

        coordinator.HandleHoldStarted();
        await coordinator.HandleHoldEndedAsync();

        Assert.Equal(VoiceOverlayState.Unavailable, overlay.State);
        Assert.Contains("No speech heard", overlay.StatusText);
        // And nothing was sent: an empty utterance must not reach the assistant as a blank turn.
        await assistant.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHoldThatTranscribedSomething_SendsIt_AndClearsThePill()
    {
        // The other side of the same branch: a rule that reported "no speech heard" for everything would pass the
        // test above and ship an assistant that never hears anything.
        var (coordinator, overlay, assistant, pushToTalk, _, _) = _Coordinator();
        pushToTalk.EndHoldAsync().Returns("wat is de status van AC-223");

        coordinator.HandleHoldStarted();
        await coordinator.HandleHoldEndedAsync();

        await assistant.Received().SendAsync("wat is de status van AC-223", Arg.Any<CancellationToken>());
        Assert.Equal(VoiceOverlayState.Hidden, overlay.State);
    }

    /// <summary>
    /// Holding the key to talk stops whatever the assistant is still reading out (AC-545 live test): the coordinator
    /// promised this in a comment and never did it, so an interrupted answer went on narrating over the question.
    /// </summary>
    [Fact]
    public void HoldStarted_StopsTheReadAloud_SoTheAssistantDoesNotTalkOverTheOperator()
    {
        var (coordinator, _, _, _, playback, _) = _Coordinator();

        coordinator.HandleHoldStarted();

        // Unconditional on purpose: unlike open-mic's barge-in there is no threshold to weigh, because a held
        // hotkey cannot be a cough.
        playback.Received(1).StopAll();
    }

    /// <summary>
    /// AC-577: a failed hold's own message must make way for a read-aloud that starts while it is still up,
    /// rather than hiding it for good — see <see cref="VoiceOverlayCoordinator"/>'s priority rule. Real production
    /// behaviour waits four seconds for the message to clear; the tests shorten that the same way
    /// <see cref="Cockpit.Core.Tests.Sessions.ScheduledResumeCoordinatorTests"/> shortens its tick interval — but
    /// what makes the assertion below sound is awaiting the coordinator's own clear task, not this number. A
    /// wall-clock budget "long enough" for the linger is a guess that loses on a loaded first run, which is what
    /// the first version of this test did.
    /// </summary>
    private static readonly TimeSpan TestLinger = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task AfterAHoldThatTranscribedNothing_AReadAloudThatStartsMeanwhile_ReachesThePill()
    {
        // Measured on a running dev build (Raymond): push-to-talk's own "keep holding" message stayed on the pill
        // forever, because neither failure branch cleared it — so a read-aloud that started afterwards played its
        // audio while the pill kept showing the stale push-to-talk message. Without the fix this never turns
        // Speaking: the message has nothing that would ever clear it.
        var (coordinator, overlay, _, pushToTalk, _, overlayCoordinator) = _Coordinator();
        pushToTalk.EndHoldAsync().Returns(string.Empty);

        coordinator.HandleHoldStarted();
        await coordinator.HandleHoldEndedAsync();
        Assert.Equal(VoiceOverlayState.Unavailable, overlay.State);

        overlayCoordinator.SetReadAloud(VoiceOverlayState.Speaking);
        await coordinator.PendingLingerClear;

        Assert.Equal(VoiceOverlayState.Speaking, overlay.State);
    }

    private static (AssistantPushToTalkCoordinator Coordinator, VoiceOverlayViewModel Overlay,
        IAssistantSessionHost Assistant, IVoicePushToTalkService PushToTalk, IVoicePlaybackQueue Playback,
        VoiceOverlayCoordinator OverlayCoordinator) _Coordinator()
    {
        var assistant = Substitute.For<IAssistantSessionHost>();
        assistant.Activity.Returns(AssistantActivity.Ready);

        var pushToTalk = Substitute.For<IVoicePushToTalkService>();
        // True, or HandleHoldEndedAsync returns before it ever reaches the transcript at all — the "nothing was
        // captured" path, which is a different case with its own (already silent-by-design) handling.
        pushToTalk.BeginHold().Returns(true);

        var overlay = new VoiceOverlayViewModel();
        var playback = Substitute.For<IVoicePlaybackQueue>();
        var overlayCoordinator = new VoiceOverlayCoordinator(overlay, new FakeVoiceOverlayPresenter());
        var coordinator = new AssistantPushToTalkCoordinator(
            TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService()),
            assistant,
            overlayCoordinator,
            pushToTalk,
            NullLogger<AssistantPushToTalkCoordinator>.Instance,
            playback,
            messageLinger: TestLinger);

        return (coordinator, overlay, assistant, pushToTalk, playback, overlayCoordinator);
    }
}
