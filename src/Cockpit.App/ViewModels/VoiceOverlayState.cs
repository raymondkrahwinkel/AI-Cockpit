namespace Cockpit.App.ViewModels;

/// <summary>Visibility/content state of the floating voice pill (#34) — see <see cref="VoiceOverlayViewModel"/>.</summary>
/// <remarks>
/// Which of these is on screen is <see cref="Services.VoiceOverlayCoordinator"/>'s decision, not any one source's:
/// a hold, open-mic dictation and read-aloud all have something to say and only one pill to say it in.
/// </remarks>
public enum VoiceOverlayState
{
    /// <summary>Nothing to report — the overlay window is not shown.</summary>
    Hidden,

    /// <summary>A microphone is open and being listened to — a held hotkey, or open-mic hearing speech start. The pill shows the recording waveform.</summary>
    Listening,

    /// <summary>
    /// The hotkey is held but nothing is being recorded, and the pill says why — no session selected, or voice
    /// off for the one that is. It used to show <see cref="Listening"/> here regardless: a waveform sitting
    /// flat while the operator talked, and the reason written only to the log.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The hold ended but voice is still getting ready — on first use the model and a GPU runtime come down
    /// before a word can be transcribed. It is a state of its own because <see cref="Transcribing"/> used to
    /// cover this too, and spent those minutes claiming to do something it had not started.
    /// </summary>
    Preparing,

    /// <summary>The microphone closed and the transcript is being produced; the pill shows a spinner.</summary>
    Transcribing,

    /// <summary>
    /// Read-aloud is playing (Raymond, 2026-07-15: the pill is not only for what you say — it is also how you
    /// see why your microphone just went quiet, since open-mic pauses itself while the cockpit speaks).
    /// Yields to any dictation. No waveform: the playback queue reports <em>that</em> it is speaking, not how
    /// loudly, and bars driven by nothing would be decoration pretending to be a meter.
    /// </summary>
    Speaking,
}
