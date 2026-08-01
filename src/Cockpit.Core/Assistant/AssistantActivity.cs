namespace Cockpit.Core.Assistant;

/// <summary>
/// What the assistant indicator reports (AC-543, criterion 6). One enum rather than a bag of booleans because the
/// states are mutually exclusive and the indicator has one thing to say at a time.
/// </summary>
/// <remarks>
/// <see cref="Dictating"/> is the odd one out: it is not the assistant doing anything, it is <c>F9</c> dictating
/// into the selected session. It lives here because the question the indicator answers is not "is something
/// listening" but <em>"who is listening"</em> — with two microphone paths side by side, the damaging mistake is
/// words landing in the wrong place. It is shown in a different colour <em>and</em> says so in words; colour alone
/// is not enough.
/// </remarks>
public enum AssistantActivity
{
    /// <summary>Idle and reachable — hold the hotkey or click the chip and it will listen.</summary>
    Ready,

    /// <summary>
    /// The assistant hotkey is held and the microphone is open. A <em>handling</em>, not a standing state: it
    /// lasts as long as the key is down. <see cref="ListeningContinuously"/> is the standing one (criterion 19).
    /// </summary>
    Listening,

    /// <summary>
    /// The microphone is open for as long as the listening mode says so, without a key being held (criterion 19).
    /// Distinct from <see cref="Listening"/> so the indicator can show a stand rather than a moment.
    /// </summary>
    ListeningContinuously,

    /// <summary>
    /// Your turn is over and the assistant is working. Its own state because an assistant that is silent after
    /// your sentence is otherwise indistinguishable from one that never heard you — the same reason
    /// <c>IEmbeddedSession</c> carries a separate activity signal.
    /// </summary>
    Thinking,

    /// <summary>Read-aloud is playing the assistant's reply.</summary>
    Speaking,

    /// <summary>
    /// The assistant has stopped and is waiting for the operator — a tool it wants to run needs an Allow, or it has
    /// asked a question. Its own state, and not folded into <see cref="Ready"/> or <see cref="Thinking"/>, because
    /// both of those are wrong in the way that costs the most: Ready says nothing is happening, so the operator
    /// does not look, and the assistant waits indefinitely on an approval nobody knows it asked for; Thinking says
    /// it is working, which is the same silence with a different label. This is the one state where the chip is not
    /// reporting but asking.
    /// </summary>
    AwaitingOperator,

    /// <summary>
    /// <c>F9</c> is dictating into the selected session — <em>not</em> the assistant. See the remarks on this enum
    /// for why a dictation state lives on the assistant indicator at all.
    /// </summary>
    Dictating,

    /// <summary>
    /// The assistant cannot be reached: the feature is switched off, no profile is set, or the instance failed to
    /// start. The indicator carries the reason alongside — an unavailable chip that does not say why sends the
    /// operator looking through Options for a setting that is not the problem.
    /// </summary>
    Unavailable,
}
