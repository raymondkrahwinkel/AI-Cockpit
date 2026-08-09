namespace Cockpit.App.ViewModels;

// Visibility/content state of the floating voice pill (#34) — see `VoiceOverlayViewModel`.
// Which of these is on screen is `Services.VoiceOverlayCoordinator`'s decision, not any one source's:
// a hold, open-mic dictation and read-aloud all have something to say and only one pill to say it in.
public enum VoiceOverlayState
{
    // Nothing to report — the overlay window is not shown.
    Hidden,

    // A microphone is open and being listened to — a held hotkey, or open-mic hearing speech start. The pill shows the recording waveform.
    Listening,

    // The hotkey is held but nothing is being recorded, and the pill says why — no session selected, or voice
    // off for the one that is. It used to show `Listening` here regardless: a waveform sitting
    // flat while the operator talked, and the reason written only to the log.
    Unavailable,

    // The hold ended but voice is still getting ready — on first use the model and a GPU runtime come down
    // before a word can be transcribed. It is a state of its own because `Transcribing` used to
    // cover this too, and spent those minutes claiming to do something it had not started.
    Preparing,

    // The microphone closed and the transcript is being produced; the pill shows a spinner.
    Transcribing,

    // The dictation is over and produced nothing — the transcription failed, or the capture held no speech — and
    // the pill says which. Its own state next to `Unavailable`: that one is a hold that never opened a
    // microphone, this one is a hold that did and came back empty. It is the answer to the only question the pill
    // could not answer before, which is what an operator asks after talking into it for a minute (AC-557).
    Failed,

    // Read-aloud is playing (Raymond, 2026-07-15: the pill is not only for what you say — it is also how you
    // see why your microphone just went quiet, since open-mic pauses itself while the cockpit speaks).
    // Yields to any dictation. No waveform: the playback queue reports *that* it is speaking, not how
    // loudly, and bars driven by nothing would be decoration pretending to be a meter.
    Speaking,
}
