using Cockpit.Core.Profiles;

namespace Cockpit.Core.Assistant;

// The Assistant Profile: a *slot* with a replaceable `SessionProfile` record behind it (AC-543).
// A `SessionProfile` cannot change provider, so a provider switch mints a *new* record and repoints the
// slot, keeping that invariant. Found by id, not label, so AC-410's rename bug cannot reach it.
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
