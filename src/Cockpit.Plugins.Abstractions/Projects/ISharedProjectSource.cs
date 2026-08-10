namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// A place a plugin can list projects it shares elsewhere but this machine has not bound to a local
/// <c>Project</c> yet (AC-245: AC-242's Depot sync, say) — registered through
/// <see cref="ICockpitHost.AddSharedProjectSource"/>, so the Projects workspace can offer them beside the local
/// ones without depending on the plugin that contributes them.
/// </summary>
public interface ISharedProjectSource
{
    /// <summary>
    /// Stable key this source is registered and removed under (<see cref="ICockpitHost.RemoveSharedProjectSource"/>)
    /// — a connection's own memory-source scheme is a natural choice for a plugin that also registers one, since it
    /// keeps <see cref="SharedProject.Id"/> lined up with what a bound project's <c>MemoryRef</c> would read, but
    /// any string unique to this plugin's own sources works.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Groups this source's rows under a heading in the Projects workspace — "Depot — Work". Keep it short: it is a
    /// section title, not a sentence. Known ahead of <see cref="ListAsync"/> so a heading (and, on failure, which
    /// source failed) can be shown without waiting on the call that fills the rows under it.
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// This source's current projects. Called each time the Projects workspace loads — there is no push/invalidate
    /// path, so a connection added, renamed or removed is reflected the next time this runs rather than while a
    /// window stays open across the change. Must not throw for an ordinary failure (unreachable, not signed in):
    /// report it through <see cref="SharedProjectListResult.Failed"/> instead, so one broken source does not cost
    /// every other source's rows.
    /// </summary>
    Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads enough of <paramref name="id"/>'s own portable definition to bind it to a local <c>Project</c> (AC-246)
    /// — called once, when the operator opens "Finish setting up…" on a row this source listed. A one-time read, not
    /// a live handle: the host copies what comes back into an ordinary local project and does not call this again to
    /// keep it current afterward — see <see cref="SharedProjectBinding"/>'s own remarks on why that is a different
    /// concern (a source's own write/conflict path, if it has one) from this one (making the project usable at all).
    /// <para>
    /// Must not throw for an ordinary failure (unreachable, not signed in, the project no longer exists, or was
    /// removed between <see cref="ListAsync"/> and this call) — report it through
    /// <see cref="SharedProjectBindingResult.Failed"/> instead, the same contract <see cref="ListAsync"/> already
    /// keeps for a whole source's own failure.
    /// </para>
    /// </summary>
    /// <param name="id">A <see cref="SharedProject.Id"/> this source itself listed — never one <see cref="ListAsync"/> never returned.</param>
    Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an edit to <paramref name="id"/>'s own claimed fields back to this source (AC-247) — called once,
    /// when the operator saves the project editor for an already-bound project whose fields
    /// <c>ProjectsViewModel._ClaimBoundProjects</c> claimed as this source's. Every field this source's own
    /// portable definition carries but <see cref="SharedProjectDefinitionEdit"/> does not mention (<c>GitUrl</c>,
    /// <c>Resources</c>, a logo) must be carried through untouched, never dropped just because this call did not
    /// name them.
    /// <para>
    /// Must not throw for an ordinary failure — report it through <see cref="SharedProjectWriteBackResult.Failed"/>,
    /// the same contract <see cref="ListAsync"/> and <see cref="PrepareBindingAsync"/> already keep for their own
    /// failures. A permission failure and a checksum conflict are told apart
    /// (<see cref="SharedProjectWriteBackOutcome.PermissionDenied"/> vs. <see cref="SharedProjectWriteBackOutcome.ChecksumConflict"/>)
    /// wherever this source can tell them apart — a caller that cannot distinguish "no reason to retry" from "reread
    /// and offer a merge" cannot honour AC-247's own "never silently overwrite" rule.
    /// </para>
    /// </summary>
    /// <param name="id">A <see cref="SharedProject.Id"/> this source itself listed.</param>
    /// <param name="edit">The operator's edited values.</param>
    /// <param name="baseChecksum">
    /// The <see cref="SharedProjectBinding.Checksum"/> from the read the operator's edit started from — sent
    /// unmodified, however long the editor stayed open, so this source's own optimistic-concurrency check answers
    /// the question it exists to answer: did anything change since the operator started editing, not since a
    /// moment ago.
    /// </param>
    Task<SharedProjectWriteBackResult> WriteBackAsync(string id, SharedProjectDefinitionEdit edit, string baseChecksum, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this source can turn a not-yet-shared local project into a new one of its own (AC-620). Required,
    /// not default-implemented — the same "an interface the plugin implements is worse for an older binary, not
    /// better" reasoning <see cref="PrepareBindingAsync"/>'s own version-history entry already documents for this
    /// interface: nothing here depends on an old plugin binary satisfying a newer host, so a plain required member
    /// stays unambiguous instead of quietly degrading to "unsupported" the way a default body would.
    /// </summary>
    bool CanPublish { get; }

    /// <summary>
    /// Places the operator could publish a project into right now — the "Depot project" picker's own rows. Not the
    /// same list <see cref="ListAsync"/> returns (that only lists targets that already carry a portable definition);
    /// a target here may or may not have one yet, which is exactly what <see cref="PublishAsync"/> checks at write
    /// time. Never called when <see cref="CanPublish"/> is false. Must not throw for an ordinary failure — report it
    /// through <see cref="SharedProjectPublishTargetListResult.Failed"/>, the same contract every other member of
    /// this interface already keeps.
    /// </summary>
    Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publishes <paramref name="definition"/> as a brand-new portable definition at <paramref name="targetId"/>
    /// (AC-620) — called once, when the operator confirms the Share dialog for a project that has never been shared
    /// this way before. Must fail with <see cref="SharedProjectPublishResult.AlreadyPublished"/>, never overwrite,
    /// when the target already carries a definition — that case is <see cref="PrepareBindingAsync"/>'s to handle, not
    /// this call's. Never called when <see cref="CanPublish"/> is false. Must not throw for an ordinary failure —
    /// report it through <see cref="SharedProjectPublishResult.Failed"/>.
    /// </summary>
    /// <param name="targetId">A <see cref="SharedProjectPublishTarget.Id"/> this source itself listed.</param>
    /// <param name="definition">The local project's portable snapshot, offered as-is — see <see cref="SharedProjectPublishDefinition"/> for what "portable" already excludes.</param>
    Task<SharedProjectPublishResult> PublishAsync(string targetId, SharedProjectPublishDefinition definition, CancellationToken cancellationToken);
}
