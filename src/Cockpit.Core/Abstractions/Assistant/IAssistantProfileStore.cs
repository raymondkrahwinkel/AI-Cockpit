using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// Loads the <see cref="AssistantProfileSlot"/> and repoints it at another record (AC-543).
/// </summary>
/// <remarks>
/// <b>There is no delete.</b> The slot cannot be removed, and the laziest way to enforce that is to offer no
/// method for it: a guard can be forgotten, bypassed by a second call site, or dropped in a refactor, while an
/// absent method cannot be called at all. The slot lives in its own <c>assistantProfile</c> section rather than
/// in the profile list for the same reason — see <see cref="AssistantProfileSlot"/>.
/// <para>
/// <b>There is no way to change a record's provider either.</b> <see cref="RepointAsync"/> takes a whole
/// <see cref="SessionProfile"/>, never a provider config to apply to the one already stored, so the
/// <c>with { ProviderConfig = … }</c> that <see cref="SessionProfile.ProviderConfig"/>'s own doc-comment forbids
/// has nothing to attach to here. Switching provider means minting a new record and handing it over.
/// </para>
/// <para>
/// <b>The two writes are the whole of criterion 4.</b> A provider switch that fails simply never reaches
/// <see cref="RepointAsync"/>, so the old record stays exactly where it was; a switch that has to give up says so
/// through <see cref="UnsetAsync"/>, which will not accept a blank reason. Neither path can leave the operator
/// with an empty slot and no explanation, because neither path can express one.
/// </para>
/// </remarks>
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
    Task<AssistantProfileSlot> RepointAsync(SessionProfile record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lands the slot explicitly on "not set up", with <paramref name="reason"/> saying why. For the switch that
    /// could not be completed and has no old record worth keeping — the alternative the operator is owed instead
    /// of a slot that is merely empty.
    /// </summary>
    /// <param name="reason">Why the slot has no record, in words for the operator. Blank is rejected: a reason nobody can read is the failure mode this method exists to avoid.</param>
    Task<AssistantProfileSlot> UnsetAsync(string reason, CancellationToken cancellationToken = default);
}
