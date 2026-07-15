namespace Cockpit.App.ViewModels;

/// <summary>Visibility/content state of the floating voice-input pill (#34) — see <see cref="VoiceOverlayViewModel"/>.</summary>
public enum VoiceOverlayState
{
    /// <summary>No hold in progress — the overlay window is not shown.</summary>
    Hidden,

    /// <summary>The push-to-talk hotkey is held down; the pill shows the recording waveform.</summary>
    Listening,

    /// <summary>
    /// The hold ended but voice is still getting ready — on first use the model and a GPU runtime come down
    /// before a word can be transcribed. It is a state of its own because <see cref="Transcribing"/> used to
    /// cover this too, and spent those minutes claiming to do something it had not started.
    /// </summary>
    Preparing,

    /// <summary>The hotkey was released and the transcript is being produced; the pill shows a spinner.</summary>
    Transcribing,
}
