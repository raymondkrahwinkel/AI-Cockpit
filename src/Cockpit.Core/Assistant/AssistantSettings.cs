using Cockpit.Core.Sessions;

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
    /// The reading level (AC-138) the assistant chat window renders replies at — the same
    /// <see cref="Sessions.ReadingLevel"/> an SDK session's own header "View" dropdown uses, and the same default
    /// (<see cref="Sessions.ReadingLevel.Developer"/>) so nobody's existing view shifts. Set only here: the chat
    /// window is a display, not a control panel, so it deliberately carries no picker of its own.
    /// </summary>
    public ReadingLevel ReadingLevel { get; init; } = ReadingLevel.Developer;

    /// <summary>
    /// Whether the operator has already been told what leaving the microphone open means (criterion 18). Set the
    /// first time <see cref="AssistantListeningMode.AlwaysOn"/> is switched on, and never asked again: a warning
    /// that returns every time is one that gets clicked away without being read.
    /// </summary>
    public bool AlwaysOnCostAcknowledged { get; init; }

    /// <summary>
    /// The sources whose <c>ConsentRisk.LowRisk</c> consent cards the assistant may skip (#AC-575), keyed the way
    /// <c>ConsentService</c> keys them: the host-stamped plugin id, or the source label for a host-internal caller
    /// (<see cref="Consent.ConsentSourceCatalog"/>). Empty by default — nothing is bypassed until the operator says so.
    /// </summary>
    /// <remarks>
    /// <b>Nothing writes this but the operator.</b> There is deliberately no MCP tool anywhere that saves
    /// <see cref="AssistantSettings"/>, so the assistant cannot widen its own permissions by being asked to, or by
    /// being talked into it. A spoken "yes" is an answer to the SDK's own permission prompt, one layer above this,
    /// and never reaches here. <c>AssistantSettingsWritersTests</c> attacks exactly that claim.
    /// <para>
    /// <b>No expiry, on purpose.</b> Not "this session" / "today" / "permanently": a third axis on top of source
    /// and risk makes the setting unreadable, and an operator who cannot read a security setting cannot check it.
    /// On until it is switched off.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> ConsentBypassSources { get; init; } = [];

    /// <summary>
    /// The sources whose <c>ConsentRisk.Dangerous</c> cards may be skipped too — a shell command, a session
    /// hand-off with the operator's rights, arbitrary egress. A <b>second, separate</b> switch per source, off by
    /// default, and never implied by <see cref="ConsentBypassSources"/>.
    /// </summary>
    /// <remarks>
    /// Two switches rather than one three-state picker: a dropdown puts "everything" one mouse movement away from
    /// "the harmless things", and the whole distinction this list draws is that those are not the same decision.
    /// </remarks>
    public IReadOnlyList<string> ConsentBypassDangerousSources { get; init; } = [];

    /// <summary>
    /// Whether any source is switched on at all. What the chip and the chat window's header report, so the fact
    /// that some consent cards are being skipped is visible without opening Options — a security setting nobody
    /// can see from the surface it affects is one that gets left on by accident.
    /// </summary>
    public bool HasConsentBypass => ConsentBypassSources.Count > 0 || ConsentBypassDangerousSources.Count > 0;
}
