using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The rule that decides what the one pill says when three sources want it (Raymond, 2026-07-15: "STT heeft
/// voorrang in de overlay over TTS"). Before this, push-to-talk simply owned the overlay and the other two did
/// not know it existed — open-mic dictated invisibly, and read-aloud said nothing at all.
/// </summary>
public class VoiceOverlayCoordinatorTests
{
    [Fact]
    public void OpenMicSpeech_ShowsThePill()
    {
        var coordinator = _Create(out var overlay, out var presenter);

        coordinator.SetOpenMic(VoiceOverlayState.Listening);

        Assert.Equal(VoiceOverlayState.Listening, overlay.State);
        Assert.Equal(1, presenter.ShowCallCount);
    }

    [Fact]
    public void ReadAloud_ShowsThePill_WhenNothingIsBeingDictated()
    {
        var coordinator = _Create(out var overlay, out var presenter);

        coordinator.SetReadAloud(VoiceOverlayState.Speaking);

        Assert.Equal(VoiceOverlayState.Speaking, overlay.State);
        Assert.Equal(1, presenter.ShowCallCount);
    }

    [Fact]
    public void ReadAloudPreparing_ShowsThePillWithAStatus_BeforeItSpeaks()
    {
        var coordinator = _Create(out var overlay, out _);

        coordinator.SetReadAloud(VoiceOverlayState.Preparing, "Preparing…");

        Assert.Equal(VoiceOverlayState.Preparing, overlay.State);
        Assert.Equal("Preparing…", overlay.StatusText);
    }

    /// <summary>Raymond's rule: what you are saying outranks what the cockpit is saying.</summary>
    [Fact]
    public void AHoldDuringReadAloud_TakesThePill_BecauseSpeechToTextOutranksTextToSpeech()
    {
        var coordinator = _Create(out var overlay, out _);
        coordinator.SetReadAloud(VoiceOverlayState.Speaking);

        coordinator.SetPushToTalk(VoiceOverlayState.Listening);

        Assert.Equal(VoiceOverlayState.Listening, overlay.State);
    }

    [Fact]
    public void OpenMicSpeech_AlsoOutranksReadAloud()
    {
        var coordinator = _Create(out var overlay, out _);
        coordinator.SetReadAloud(VoiceOverlayState.Speaking);

        coordinator.SetOpenMic(VoiceOverlayState.Listening);

        Assert.Equal(VoiceOverlayState.Listening, overlay.State);
    }

    /// <summary>Both are dictation, but one of them you asked for by holding a key.</summary>
    [Fact]
    public void AHold_OutranksOpenMic()
    {
        var coordinator = _Create(out var overlay, out _);
        coordinator.SetOpenMic(VoiceOverlayState.Transcribing);

        coordinator.SetPushToTalk(VoiceOverlayState.Listening);

        Assert.Equal(VoiceOverlayState.Listening, overlay.State);
    }

    /// <summary>
    /// The failure this class exists to prevent: a hold ends while open-mic is mid-transcription, and the pill
    /// vanishes over a sentence still being produced. Every source writing the overlay directly is exactly how
    /// that happens.
    /// </summary>
    [Fact]
    public void AHoldEnding_DoesNotTakeThePillFromOpenMic()
    {
        var coordinator = _Create(out var overlay, out var presenter);
        coordinator.SetOpenMic(VoiceOverlayState.Transcribing);
        coordinator.SetPushToTalk(VoiceOverlayState.Listening);

        coordinator.SetPushToTalk(null);

        Assert.Equal(VoiceOverlayState.Transcribing, overlay.State);
        Assert.Equal(0, presenter.HideCallCount);
    }

    /// <summary>Same shape the other way: dictation ending hands the pill back to read-aloud rather than hiding it.</summary>
    [Fact]
    public void DictationEndingWhileReadAloudPlays_HandsThePillBack_RatherThanHidingIt()
    {
        var coordinator = _Create(out var overlay, out var presenter);
        coordinator.SetReadAloud(VoiceOverlayState.Speaking);
        coordinator.SetPushToTalk(VoiceOverlayState.Listening);

        coordinator.SetPushToTalk(null);

        Assert.Equal(VoiceOverlayState.Speaking, overlay.State);
        Assert.Equal(0, presenter.HideCallCount);
    }

    [Fact]
    public void WithNothingToReport_ThePillGoesAway()
    {
        var coordinator = _Create(out var overlay, out var presenter);
        coordinator.SetPushToTalk(VoiceOverlayState.Listening);

        coordinator.SetPushToTalk(null);

        Assert.Equal(VoiceOverlayState.Hidden, overlay.State);
        Assert.Equal(1, presenter.HideCallCount);
    }

    /// <summary>
    /// The view model drops the text on any state that has nothing to say, so a source reporting something
    /// unrelated must not blank a download line that is still running.
    /// </summary>
    [Fact]
    public void APreparingStatus_SurvivesAnotherSourceReporting()
    {
        var coordinator = _Create(out var overlay, out _);
        coordinator.SetPushToTalk(VoiceOverlayState.Preparing, "Downloading speech model — 41%", 0.41);

        coordinator.SetReadAloud(VoiceOverlayState.Speaking);

        Assert.Equal(VoiceOverlayState.Preparing, overlay.State);
        Assert.Equal("Downloading speech model — 41%", overlay.StatusText);
        Assert.Equal(0.41, overlay.ProgressValue);
    }

    private static VoiceOverlayCoordinator _Create(out VoiceOverlayViewModel overlay, out FakeVoiceOverlayPresenter presenter)
    {
        overlay = new VoiceOverlayViewModel();
        presenter = new FakeVoiceOverlayPresenter();
        return new VoiceOverlayCoordinator(overlay, presenter);
    }
}
