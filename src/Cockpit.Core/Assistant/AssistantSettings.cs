using Cockpit.Core.Sessions;

namespace Cockpit.Core.Assistant;

// The assistant's own settings, persisted under the `assistant` section of `cockpit.json` — the same
// store pattern as `Cockpit.Core.Voice.VoiceSettings`, which this deliberately does not extend: voice
// settings are about the microphone and the speaker, and the assistant can be used with neither.
// The profile the assistant runs under is *not* here. It lives in its own section behind
// `AssistantProfileSlot`, so switching it off never risks the slot and turning the feature back on
// finds the profile still set. Off is off, not deleted.
//
// Neither is the listening mode, and for a different reason: it is not stored at all. What it says — whether the
// microphone stays open — is already one persisted flag, `VoiceSettings.OpenMicEnabled`, so
// `AssistantListeningMode` is computed from it rather than saved beside it. Two stored flags meaning
// the same thing is two chances for them to disagree, and the one that loses is whichever the operator was not
// looking at. The third mode needs a wake word to be configured, which this phase does not build, so there is
// nothing more to remember yet.
public sealed record AssistantSettings
{
    // Whether the assistant exists at all (decision 7, criterion 1). *Off by default.* Off means: no
    // instance, no model in memory, no session costing anything, no chip in the sidebar, and the assistant
    // hotkey does nothing — while saying why, rather than being a key that silently is not there.
    public bool IsEnabled { get; init; }

    // Whether replies are spoken. Its own switch rather than a consequence of `IsEnabled`: someone in
    // a shared room, or on a machine with no working audio, can want the assistant as a text assistant and
    // nothing more. Switching it off mid-sentence cuts the sentence off — whoever clicks off wants silence, not
    // one more paragraph (criterion 9).
    public bool SpeakReplies { get; init; } = true;

    // Avalonia `Key` enum name for the assistant push-to-talk hotkey. F10, next to dictation's F9, and rebindable.
    public string PushToTalkKeyName { get; init; } = "F10";

    // The reading level (AC-138) the assistant chat window renders replies at — the same
    // `Sessions.ReadingLevel` an SDK session's own header "View" dropdown uses, and the same default
    // (`Sessions.ReadingLevel.Developer`) so nobody's existing view shifts. Set only here: the chat
    // window is a display, not a control panel, so it deliberately carries no picker of its own.
    public ReadingLevel ReadingLevel { get; init; } = ReadingLevel.Developer;

    // Whether the operator has already been told what leaving the microphone open means (criterion 18). Set the
    // first time `AssistantListeningMode.AlwaysOn` is switched on, and never asked again: a warning
    // that returns every time is one that gets clicked away without being read.
    public bool AlwaysOnCostAcknowledged { get; init; }

    // Skip Cockpit's consent card for every source and both risk classes (#AC-637). *On by default*, which is
    // the one setting here that starts wide: the assistant asking about each of its own host-side actions was the
    // friction the operator wanted gone, and half a bypass — everyday skipped, dangerous still asking — is the
    // version that reads as "off" while still being on. Off falls back to the two per-source lists below, which
    // keep their own defaults and are never touched by this switch: turning it off restores exactly what was
    // ticked before it went on. The other three conditions in `AssistantConsentBypassPolicy` still hold, so this
    // is "everything the assistant asks, while the assistant is on" and never anything an ordinary pane asks.
    // The default reaches a fresh install only: a config that predates the switch reads back off and keeps asking
    // exactly as it did (`AssistantSettingsEntry.ToDomain`), because upgrading is not a decision to widen.
    public bool ConsentBypassAll { get; init; } = true;

    // The sources whose `ConsentRisk.LowRisk` consent cards the assistant may skip (#AC-575), keyed the way
    // `ConsentService` keys them: the host-stamped plugin id, or the source label for a host-internal caller
    // (`Consent.ConsentSourceCatalog`). Empty by default — with `ConsentBypassAll` off, nothing is bypassed
    // until the operator says so, source by source.
    // *Nothing writes this but the operator.* There is deliberately no MCP tool anywhere that saves
    // `AssistantSettings`, so the assistant cannot widen its own permissions by being asked to, or by
    // being talked into it. A spoken "yes" is an answer to the SDK's own permission prompt, one layer above this,
    // and never reaches here. `AssistantSettingsWritersTests` attacks exactly that claim.
    //
    // *No expiry, on purpose.* Not "this session" / "today" / "permanently": a third axis on top of source
    // and risk makes the setting unreadable, and an operator who cannot read a security setting cannot check it.
    // On until it is switched off.
    public IReadOnlyList<string> ConsentBypassSources { get; init; } = [];

    // The sources whose `ConsentRisk.Dangerous` cards may be skipped too — a shell command, a session
    // hand-off with the operator's rights, arbitrary egress. A *second, separate* switch per source, off by
    // default, and never implied by `ConsentBypassSources` (`ConsentBypassAll` covers it wholesale instead).
    // Two switches rather than one three-state picker: a dropdown puts "everything" one mouse movement away from
    // "the harmless things", and the whole distinction this list draws is that those are not the same decision.
    public IReadOnlyList<string> ConsentBypassDangerousSources { get; init; } = [];

    // Whether anything is switched on at all. What the chip and the chat window's header report, so the fact
    // that some consent cards are being skipped is visible without opening Options — which matters more now
    // that `ConsentBypassAll` starts on: this is the surface that says so without anyone having ticked anything.
    public bool HasConsentBypass =>
        ConsentBypassAll || ConsentBypassSources.Count > 0 || ConsentBypassDangerousSources.Count > 0;
}
