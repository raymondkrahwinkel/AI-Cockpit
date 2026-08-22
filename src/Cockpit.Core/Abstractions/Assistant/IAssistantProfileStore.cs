using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// Loads the <see cref="AssistantProfileSlot"/> and repoints it at another record (AC-543). No delete method is
/// offered — an absent method cannot be forgotten or bypassed the way a guard can. No changing a record's provider
/// either: <see cref="RepointAsync"/> takes a whole <see cref="SessionProfile"/>, so switching mints a new record; giving up goes through <see cref="UnsetAsync"/> (blank reasons rejected) — neither path leaves an unexplained empty slot.
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
    /// Points the slot at <paramref name="record"/> and persists it, returning the slot that resulted. A
    /// <em>new</em> record when the provider changed — the previous one is never edited into shape.
    /// <paramref name="replacesStandingInstruction"/> (AC-594) is required, not defaulted: forgetting it would silently switch the assistant back to adding, a setting changing itself behind the operator's back.
    /// </summary>
    Task<AssistantProfileSlot> RepointAsync(
        SessionProfile record,
        bool replacesStandingInstruction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lands the slot explicitly on "not set up", with <paramref name="reason"/> saying why — for the switch that
    /// could not be completed and left no old record worth keeping. Blank <paramref name="reason"/> is rejected: a
    /// reason nobody can read is the failure mode this method exists to avoid.
    /// </summary>
    Task<AssistantProfileSlot> UnsetAsync(string reason, CancellationToken cancellationToken = default);
}
