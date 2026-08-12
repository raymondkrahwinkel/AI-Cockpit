namespace Cockpit.Core.Voice;

// Who queued a read-aloud batch: the operator's own session (F9 / the read-aloud button) or the assistant reading
// its own reply (AC-729). Session is the default so every pre-existing caller of `IVoicePlaybackQueue` keeps
// behaving exactly as before without passing this — only the assistant's route opts into the other value.
public enum VoicePlaybackSource
{
    Session,
    Assistant,
}
