namespace Cockpit.Core.Assistant;

/// <summary>
/// The assistant's own settings, persisted under the <c>assistant</c> section of <c>cockpit.json</c> — the same
/// store pattern as <see cref="Cockpit.Core.Voice.VoiceSettings"/>, which this deliberately does not extend: voice
/// settings are about the microphone and the speaker, and the assistant can be used with neither.
/// </summary>
/// <remarks>
/// The profile the assistant runs under is <em>not</em> here. It lives in its own section behind
/// <see cref="AssistantProfileSlot"/>, so switching it off never risks the slot and turning the feature back on
/// finds the profile still set. Off is off, not deleted.
/// <para>
/// Neither is the listening mode, and for a different reason: it is not stored at all. What it says — whether the
/// microphone stays open — is already one persisted flag, <c>VoiceSettings.OpenMicEnabled</c>, so
/// <see cref="AssistantListeningMode"/> is computed from it rather than saved beside it. Two stored flags meaning
/// the same thing is two chances for them to disagree, and the one that loses is whichever the operator was not
/// looking at. The third mode needs a wake word to be configured, which this phase does not build, so there is
/// nothing more to remember yet.
/// </para>
/// </remarks>
public sealed record AssistantSettings
{
    /// <summary>
    /// Whether the assistant exists at all (decision 7, criterion 1). <b>Off by default.</b> Off means: no
    /// instance, no model in memory, no session costing anything, no chip in the sidebar, and the assistant
    /// hotkey does nothing — while saying why, rather than being a key that silently is not there.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Whether replies are spoken. Its own switch rather than a consequence of <see cref="IsEnabled"/>: someone in
    /// a shared room, or on a machine with no working audio, can want the assistant as a text assistant and
    /// nothing more. Switching it off mid-sentence cuts the sentence off — whoever clicks off wants silence, not
    /// one more paragraph (criterion 9).
    /// </summary>
    public bool SpeakReplies { get; init; } = true;

    /// <summary>Avalonia <c>Key</c> enum name for the assistant push-to-talk hotkey. F10, next to dictation's F9, and rebindable.</summary>
    public string PushToTalkKeyName { get; init; } = "F10";

    /// <summary>
    /// Whether the operator has already been told what leaving the microphone open means (criterion 18). Set the
    /// first time <see cref="AssistantListeningMode.AlwaysOn"/> is switched on, and never asked again: a warning
    /// that returns every time is one that gets clicked away without being read.
    /// </summary>
    public bool AlwaysOnCostAcknowledged { get; init; }
}
