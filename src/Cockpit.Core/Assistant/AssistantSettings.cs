using Cockpit.Core.Sessions;

namespace Cockpit.Core.Assistant;

// The assistant's own settings, persisted under `assistant` in `cockpit.json`, deliberately not extending
// `Cockpit.Core.Voice.VoiceSettings` since the assistant can be used with neither mic nor speaker. The
// profile lives behind `AssistantProfileSlot`; the listening mode is computed, not stored, from `VoiceSettings.OpenMicEnabled`.
public sealed record AssistantSettings
{
    // Whether the assistant exists at all (decision 7, criterion 1). *Off by default.* Off means: no
    // instance, no model in memory, no session costing anything, no chip in the sidebar, and the assistant
    // hotkey does nothing — while saying why, rather than being a key that silently is not there.
    public bool IsEnabled { get; init; }

    // Whether replies are spoken. Its own switch, not a consequence of `IsEnabled`: a shared room or a
    // machine with no working audio can still want a text assistant. Switching off mid-sentence cuts it
    // off — whoever clicks off wants silence, not one more paragraph (criterion 9).
    public bool SpeakReplies { get; init; } = true;

    // Avalonia `Key` enum name for the assistant push-to-talk hotkey. F10, next to dictation's F9, and rebindable.
    public string PushToTalkKeyName { get; init; } = "F10";

    // Whether the chat pop-out stays above every other window (AC-681). *On by default* — this is the behaviour
    // the window always had before this switch existed, so upgrading a config that predates it must not change
    // anything until the operator actually opens Options and turns it off.
    public bool AlwaysOnTop { get; init; } = true;

    // The reading level (AC-138) the assistant chat window renders replies at — the same `Sessions.ReadingLevel`
    // and the same default as an SDK session's "View" dropdown, so nobody's existing view shifts. Set only
    // here: the chat window is a display, not a control panel, and deliberately carries no picker of its own.
    public ReadingLevel ReadingLevel { get; init; } = ReadingLevel.Developer;

    // Whether the operator has already been told what leaving the microphone open means (criterion 18). Set the
    // first time `AssistantListeningMode.AlwaysOn` is switched on, and never asked again: a warning
    // that returns every time is one that gets clicked away without being read.
    public bool AlwaysOnCostAcknowledged { get; init; }

    // Skip Cockpit's consent card for every source and both risk classes (#AC-637). *On by default* — the
    // friction the operator wanted gone — falling back to the two per-source lists below when off, which
    // keep their own defaults untouched. A config that predates the switch reads back off, never upgraded to on.
    public bool ConsentBypassAll { get; init; } = true;

    // The sources whose `ConsentRisk.LowRisk` cards the assistant may skip (#AC-575), keyed as `ConsentService`
    // keys them. Empty by default. *Nothing writes this but the operator* — no MCP tool saves `AssistantSettings`,
    // so the assistant cannot widen its own permissions (`AssistantSettingsWritersTests`). *No expiry, on purpose.*
    public IReadOnlyList<string> ConsentBypassSources { get; init; } = [];

    // The sources whose `ConsentRisk.Dangerous` cards may be skipped too. A *second, separate* switch per
    // source, off by default and never implied by `ConsentBypassSources` — a dropdown would put "everything"
    // one mouse movement from "the harmless things", collapsing a distinction that matters.
    public IReadOnlyList<string> ConsentBypassDangerousSources { get; init; } = [];

    // Whether anything is switched on at all. What the chip and the chat window's header report, so the fact
    // that some consent cards are being skipped is visible without opening Options — which matters more now
    // that `ConsentBypassAll` starts on: this is the surface that says so without anyone having ticked anything.
    public bool HasConsentBypass =>
        ConsentBypassAll || ConsentBypassSources.Count > 0 || ConsentBypassDangerousSources.Count > 0;
}
