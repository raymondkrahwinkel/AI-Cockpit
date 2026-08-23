using Cockpit.Core.Assistant;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of the `AssistantProfileSlot`, its own section, deliberately not an entry in `profiles`.
// Round-trips through `SessionProfileEntry` rather than a second mapping, so the two never drift apart.
internal sealed class AssistantProfileEntry
{
    // Default reason for a slot with no record and none given (fresh install or hand-edit); filled in on
    // read rather than left null, since a null reason with no record is a blank row criterion 4 rules out.
    internal const string NoRecordYetReason = "No assistant profile has been set up yet.";

    // The record the slot points at; absent means the slot is unset and `UnsetReason` says why.
    public SessionProfileEntry? Profile { get; set; }

    // Why `Profile` is absent. Written only when it is; ignored when a record is present.
    public string? UnsetReason { get; set; }

    // Whether the record's system prompt replaces the built-in standing instruction (AC-594); absent reads as false, which is "add to it".
    public bool? ReplacesStandingInstruction { get; set; }

    public static AssistantProfileEntry FromDomain(AssistantProfileSlot slot) => new()
    {
        Profile = slot.Profile is null ? null : SessionProfileEntry.FromDomain(slot.Profile),
        // A configured slot writes no reason: keeping a stale one would leave the file claiming both a record and
        // an explanation for its absence, and whichever a later reader believed would be the wrong one.
        UnsetReason = slot.Profile is null ? slot.UnsetReason ?? NoRecordYetReason : null,
        ReplacesStandingInstruction = slot.ReplacesStandingInstruction,
    };

    public AssistantProfileSlot ToDomain() => Profile is { } profile
        ? new AssistantProfileSlot(profile.ToDomain(), null, ReplacesStandingInstruction ?? false)
        : new AssistantProfileSlot(null, string.IsNullOrWhiteSpace(UnsetReason) ? NoRecordYetReason : UnsetReason);
}
