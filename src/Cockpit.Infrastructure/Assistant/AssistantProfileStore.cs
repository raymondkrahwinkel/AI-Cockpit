using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Assistant;

/// <summary>
/// Persists the <see cref="AssistantProfileSlot"/> under the <c>assistantProfile</c> section of
/// <c>cockpit.json</c>, read-modify-write through <see cref="CockpitConfigFileAccess"/> like every other
/// section-owning store (same pattern as <see cref="Sessions.SessionProfileStore"/>).
/// <para>
/// <b>Its own section, not an entry in <c>profiles</c>.</b> That placement is what makes three acceptance
/// criteria hold without a single guard: the slot cannot be deleted through the profile list, it does not appear
/// in <em>+ New session</em>, and <c>list_profiles</c> never offers it as a delegation target — all three read
/// <see cref="Cockpit.Core.Abstractions.Profiles.ISessionProfileStore.LoadAsync"/>, and this is not in what that
/// returns. It is also why a rename cannot lose the slot: nothing matches it by label, so AC-410's
/// rename-reads-as-gone bug has no surface here.
/// </para>
/// </summary>
internal sealed class AssistantProfileStore : IAssistantProfileStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public AssistantProfileStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal AssistantProfileStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<AssistantProfileSlot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);

        // No section yet is a fresh install, not a fault — but it still comes back with a reason rather than a
        // bare empty slot, so every caller has something to show and none has to invent wording of its own.
        return configFile?.AssistantProfile?.ToDomain()
            ?? new AssistantProfileSlot(null, AssistantProfileEntry.NoRecordYetReason);
    }

    public Task<AssistantProfileSlot> RepointAsync(SessionProfile record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // The whole record is replaced, never edited: this is the only write that produces a configured slot, and
        // it cannot express "keep the record, change its provider" — which is the mutation SessionProfile forbids.
        return _WriteAsync(new AssistantProfileSlot(record), cancellationToken);
    }

    public Task<AssistantProfileSlot> UnsetAsync(string reason, CancellationToken cancellationToken = default)
    {
        // Rejected rather than defaulted: a caller that gives up on a switch knows why, and silently substituting
        // generic wording here would turn a fixable report into "not set up yet" for the operator to puzzle over.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return _WriteAsync(new AssistantProfileSlot(null, reason), cancellationToken);
    }

    /// <summary>Writes the section and hands back what was written, so a caller need not re-read to see where the slot landed.</summary>
    private async Task<AssistantProfileSlot> _WriteAsync(AssistantProfileSlot slot, CancellationToken cancellationToken)
    {
        await _configFile.UpdateAsync(
            file => file.AssistantProfile = AssistantProfileEntry.FromDomain(slot),
            cancellationToken).ConfigureAwait(false);

        return slot;
    }
}
