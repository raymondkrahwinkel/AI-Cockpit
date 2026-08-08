namespace Cockpit.Core.Assistant;

// What the assistant indicator reports (AC-543, criterion 6). One enum rather than a bag of booleans because the
// states are mutually exclusive and the indicator has one thing to say at a time.
// `Dictating` is the odd one out: it is not the assistant doing anything, it is `F9` dictating
// into the selected session. It lives here because the question the indicator answers is not "is something
// listening" but *"who is listening"* — with two microphone paths side by side, the damaging mistake is
// words landing in the wrong place. It is shown in a different colour *and* says so in words; colour alone
// is not enough.
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

    // The words are being turned into text. Between a hold ending (or open-mic hearing you stop) and the
    // assistant getting anything to think about — a wait with nothing else on screen to explain it.
    // Used to live on the floating voice pill; moved here (Raymond, 2026-08-08) because everything the assistant
    // is doing belongs on the assistant's own chip. The pill still carries it for `F9` dictation into a session.
    Transcribing,

    // Speech-to-text is fetching what it needs before it can transcribe at all — on first use that is a ~1.6 GB
    // model and a GPU runtime. Its own state rather than a differently-worded Transcribing: this one can last
    // minutes and has a step and a percentage to show (`PreparationStatus`), and a chip that claimed to be
    // transcribing for four minutes would be lying about which part is slow.
    Preparing,

    // Your turn is over and the assistant is working. Its own state because an assistant that is silent after
    // your sentence is otherwise indistinguishable from one that never heard you — the same reason
    // `IEmbeddedSession` carries a separate activity signal.
    Thinking,

    // Read-aloud is playing the assistant's reply.
    Speaking,

    // The assistant has stopped and is waiting for the operator — a tool it wants to run needs an Allow, or it has
    // asked a question. Its own state, and not folded into `Ready` or `Thinking`, because
    // both of those are wrong in the way that costs the most: Ready says nothing is happening, so the operator
    // does not look, and the assistant waits indefinitely on an approval nobody knows it asked for; Thinking says
    // it is working, which is the same silence with a different label. This is the one state where the chip is not
    // reporting but asking.
    AwaitingOperator,

    // `F9` is dictating into the selected session — *not* the assistant. See the remarks on this enum
    // for why a dictation state lives on the assistant indicator at all.
    Dictating,

    // The assistant cannot be reached: the feature is switched off, no profile is set, or the instance failed to
    // start. The indicator carries the reason alongside — an unavailable chip that does not say why sends the
    // operator looking through Options for a setting that is not the problem.
    Unavailable,
}
