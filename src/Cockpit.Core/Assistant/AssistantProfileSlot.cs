using Cockpit.Core.Profiles;

namespace Cockpit.Core.Assistant;

// The Assistant Profile: a *slot* with a replaceable `SessionProfile` record behind it (AC-543).
// A `SessionProfile` cannot change provider — "a different provider means a new profile, so credentials
// and configuration never end up describing a backend the profile no longer talks to". But the assistant must be
// switchable between Claude, Codex and a local model. Two layers resolve that without weakening the invariant:
//   - the *slot* — this type — has a stable id (`SlotId`), a fixed display name
//     (`DisplayName`), and cannot be deleted;
//   - the *record* — `Profile` — is an ordinary profile with a fixed provider. Switching
//     provider mints a *new* record and repoints the slot at it; the old record is dropped afterwards.
//
// *The invariant, stated so it can be tested:* no `SessionProfile` record ever changes provider.
// A `with { ProviderConfig = … }` anywhere on the assistant path is the bug this design exists to prevent —
// it compiles, it appears to work, and it is exactly what the record's own doc-comment forbids.
//
// *Found by id, never by label.* The slot lives in its own `assistantProfile` section of
// `cockpit.json` rather than as an entry in the profile list, so there is nothing to match a label against
// and AC-410's rename-reads-as-gone bug cannot reach it. That placement is also why the slot is not deletable,
// why it does not appear in *+ New session*, and why `list_profiles` never offers it as a delegation
// target: it is not in the list those three read. Guards would have had to be added, remembered, and kept — this
// is the same property by construction.
//
// `Profile`:
// The record the slot currently points at, or `null` when the slot is deliberately unset —
// a fresh install, or a provider switch that failed and landed here with `UnsetReason` filled in.
// `UnsetReason`:
// Why `Profile` is `null`, in words for the operator. Never null while
// `Profile` is set. An empty slot with no reason is the failure mode criterion 4 rules out:
// the operator is owed either the old profile or an explanation, never a blank row.
// `ReplacesStandingInstruction`:
// Whether the record's own system prompt *replaces* `AssistantSystemPrompt.Default` instead of
// being added to it (AC-594). Default false: what an operator types is an addition.
public sealed record AssistantProfileSlot(
    SessionProfile? Profile = null,
    string? UnsetReason = null,
    bool ReplacesStandingInstruction = false)
{
    // The slot's stable identity. A constant rather than a generated id: there is exactly one slot, it is created
    // by the app rather than by the operator, and a value that could differ per machine would be one more thing
    // that can go missing.
    public const string SlotId = "cockpit_assistant_profile";

    // What the operator sees the slot called, fixed regardless of what the record behind it is labelled. The
    // record's own `SessionProfile.Label` is free to say "Claude (assistant)" or anything else and
    // may be renamed at will — nothing resolves the slot through it.
    public const string DisplayName = "Assistant Profile";

    // True when the assistant has a usable profile to run under. `false` means `UnsetReason` says why.
    public bool IsConfigured => Profile is not null;

    // The empty slot, with the reason the operator is owed. The only way to build one without a profile — the
    // default constructor would otherwise permit `new AssistantProfileSlot()`, which is precisely the blank
    // row with no explanation that criterion 4 rules out, reachable by forgetting an argument.
    public static AssistantProfileSlot Unset(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("An unset Assistant Profile must say why it is unset.", nameof(reason))
            : new AssistantProfileSlot(null, reason);
}
