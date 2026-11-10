namespace Cockpit.Core.Assistant;

// How much the assistant listens (AC-543, criteria 17–19). Set from the indicator itself rather than from a
// button elsewhere: the indicator is where the operator looks to see who is listening, so it is where they reach
// to change it.
public enum AssistantListeningMode
{
    // The default. The microphone is closed and opens only while the assistant hotkey is held. Nothing is heard
    // that was not deliberately spoken to the assistant.
    Off,

    // The microphone stays open and everything said goes to the assistant — an aside to a colleague, a phone
    // call, thinking out loud. An honest state rather than a shortcoming, but the operator is told what it means
    // and that it costs per utterance when they switch it on (criterion 18), once, not on every use.
    AlwaysOn,

    // Local transcripts reach the assistant only after the first wake word or during its follow-up period.
    AlwaysOnWithWakeWord,
}
