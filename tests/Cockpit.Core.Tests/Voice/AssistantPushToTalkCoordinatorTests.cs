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
        var (coordinator, overlay, assistant, pushToTalk) = _Coordinator();
        pushToTalk.EndHoldAsync(applyCleanup: false).Returns(string.Empty);

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
        var (coordinator, overlay, assistant, pushToTalk) = _Coordinator();
        pushToTalk.EndHoldAsync(applyCleanup: false).Returns("wat is de status van AC-223");

        coordinator.HandleHoldStarted();
        await coordinator.HandleHoldEndedAsync();

        await assistant.Received().SendAsync("wat is de status van AC-223", Arg.Any<CancellationToken>());
        Assert.Equal(VoiceOverlayState.Hidden, overlay.State);
    }

    private static (AssistantPushToTalkCoordinator Coordinator, VoiceOverlayViewModel Overlay,
        IAssistantSessionHost Assistant, IVoicePushToTalkService PushToTalk) _Coordinator()
    {
        var assistant = Substitute.For<IAssistantSessionHost>();
        assistant.Activity.Returns(AssistantActivity.Ready);

        var pushToTalk = Substitute.For<IVoicePushToTalkService>();
        // True, or HandleHoldEndedAsync returns before it ever reaches the transcript at all — the "nothing was
        // captured" path, which is a different case with its own (already silent-by-design) handling.
        pushToTalk.BeginHold().Returns(true);

        var overlay = new VoiceOverlayViewModel();
        var coordinator = new AssistantPushToTalkCoordinator(
            TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService()),
            assistant,
            new VoiceOverlayCoordinator(overlay, new FakeVoiceOverlayPresenter()),
            pushToTalk,
            NullLogger<AssistantPushToTalkCoordinator>.Instance);

        return (coordinator, overlay, assistant, pushToTalk);
    }
}
