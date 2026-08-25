namespace Cockpit.Core.Assistant;

// What the assistant indicator reports (AC-543, criterion 6). One enum rather than a bag of booleans because the
// states are mutually exclusive. `Dictating` is the odd one out: it is `F9` dictating into the selected
// session, not the assistant, shown in a different colour and in words so listening paths cannot be confused.
public enum AssistantActivity
{
    // Idle and reachable — hold the hotkey or click the chip and it will listen.
    Ready,

    // The assistant hotkey is held and the microphone is open. A *handling*, not a standing state: it
    // lasts as long as the key is down. `ListeningContinuously` is the standing one (criterion 19).
    Listening,

    // The microphone is open for as long as the listening mode says so, without a key being held (criterion 19).
    // Distinct from `Listening` so the indicator can show a stand rather than a moment.
    ListeningContinuously,

    // The words are being turned into text. Moved here from the floating voice pill (Raymond, 2026-08-08)
    // because everything the assistant is doing belongs on its own chip. The pill still carries it for
    // `F9` dictation into a session.
    Transcribing,

    // Speech-to-text is fetching what it needs before it can transcribe — on first use a ~1.6 GB model
    // and a GPU runtime. Own state rather than a differently-worded Transcribing: this can take minutes
    // and has a step/percentage to show (`PreparationStatus`).
    Preparing,

    // Your turn is over and the assistant is working. Its own state because an assistant that is silent after
    // your sentence is otherwise indistinguishable from one that never heard you — the same reason
    // `IEmbeddedSession` carries a separate activity signal.
    Thinking,

    // Read-aloud is playing the assistant's reply.
    Speaking,

    // The assistant has stopped and is waiting for the operator — a tool needs an Allow, or it asked a
    // question. Not folded into `Ready` (looks like nothing is happening) or `Thinking` (looks like it is
    // still working): this is the one state where the chip is asking, not reporting.
    AwaitingOperator,

    // `F9` is dictating into the selected session — *not* the assistant. See the remarks on this enum
    // for why a dictation state lives on the assistant indicator at all.
    Dictating,

    // The assistant cannot be reached: the feature is switched off, no profile is set, or the instance failed to
    // start. The indicator carries the reason alongside — an unavailable chip that does not say why sends the
    // operator looking through Options for a setting that is not the problem.
    Unavailable,
}
