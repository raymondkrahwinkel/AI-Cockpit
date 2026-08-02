using Cockpit.Core.Assistant;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of the <see cref="AssistantProfileSlot"/> in the <c>assistantProfile</c> section — its own
/// section, deliberately not an entry in <c>profiles</c>.
/// <para>
/// The record itself round-trips through <see cref="SessionProfileEntry"/> rather than through a second
/// serializer of its own. It is an ordinary <see cref="Cockpit.Core.Profiles.SessionProfile"/>, and a parallel
/// mapping would be a copy that drifts: the day a profile gains a field, one of the two mappings gets it and the
/// assistant's record quietly loses it on the next save.
/// </para>
/// </summary>
internal sealed class AssistantProfileEntry
{
    /// <summary>
    /// What the slot says when it has no record and the config offers no reason — a fresh install, or a config
    /// hand-edited into that state. Filled in on read rather than left null, because
    /// <see cref="AssistantProfileSlot.UnsetReason"/> being null while there is no record is the blank row
    /// criterion 4 rules out.
    /// </summary>
    internal const string NoRecordYetReason = "No assistant profile has been set up yet.";

    /// <summary>The record the slot points at; absent means the slot is unset and <see cref="UnsetReason"/> says why.</summary>
    public SessionProfileEntry? Profile { get; set; }

    /// <summary>Why <see cref="Profile"/> is absent. Written only when it is; ignored when a record is present.</summary>
    public string? UnsetReason { get; set; }

    /// <summary>Whether the record's system prompt replaces the built-in standing instruction (AC-594); absent reads as false, which is "add to it".</summary>
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
