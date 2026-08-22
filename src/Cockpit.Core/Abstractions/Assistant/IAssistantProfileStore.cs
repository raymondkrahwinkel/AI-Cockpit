using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// Loads the <see cref="AssistantProfileSlot"/> and repoints it at another record (AC-543). There is no delete: no
/// method is offered for it, since an absent method cannot be forgotten or bypassed the way a guard can — the slot
/// lives in its own <c>assistantProfile</c> section for the same reason. There is also no way to change a record's
/// provider: <see cref="RepointAsync"/> takes a whole <see cref="SessionProfile"/>, so switching means minting a
/// new record; a failed switch leaves the old one untouched, and one that must give up says so via <see cref="UnsetAsync"/> (blank reasons rejected) — neither path leaves an unexplained empty slot.
/// </summary>
public interface IAssistantProfileStore
{
    /// <summary>
    /// The slot as it stands. Never returns an unconfigured slot without an
    /// <see cref="AssistantProfileSlot.UnsetReason"/> — a fresh install, and a config hand-edited into that state,
    /// both come back with words the operator can act on rather than a blank row.
    /// </summary>
    Task<AssistantProfileSlot> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Points the slot at <paramref name="record"/> and persists it, returning the slot that resulted.
    /// </summary>
    /// <param name="record">
    /// The record the slot should resolve to from now on. A <em>new</em> record when the provider changed: the
    /// previous one is simply no longer referenced, never edited into shape.
    /// </param>
    /// <param name="replacesStandingInstruction">
    /// Whether <paramref name="record"/>'s system prompt replaces the built-in standing instruction rather than
    /// adding to it (AC-594). Required rather than defaulted: a caller that forgot it would silently switch the
    /// assistant back to adding, which is a setting changing itself behind the operator's back.
    /// </param>
    Task<AssistantProfileSlot> RepointAsync(
        SessionProfile record,
        bool replacesStandingInstruction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lands the slot explicitly on "not set up", with <paramref name="reason"/> saying why. For the switch that
    /// could not be completed and has no old record worth keeping — the alternative the operator is owed instead
    /// of a slot that is merely empty.
    /// </summary>
    /// <param name="reason">Why the slot has no record, in words for the operator. Blank is rejected: a reason nobody can read is the failure mode this method exists to avoid.</param>
    Task<AssistantProfileSlot> UnsetAsync(string reason, CancellationToken cancellationToken = default);
}
