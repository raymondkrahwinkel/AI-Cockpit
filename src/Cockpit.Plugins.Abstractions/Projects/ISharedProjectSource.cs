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
    /// Stable key this source is registered and removed under
    /// (<see cref="ICockpitHost.RemoveSharedProjectSource"/>) — any string unique to this plugin's own sources.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Groups this source's rows under a heading in the Projects workspace — "Depot — Work". Keep it short.
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// This source's current projects. Called each time the Projects workspace loads — there is no
    /// push/invalidate path.
    /// </summary>
    /// <remarks>
    /// Must not throw for an ordinary failure (unreachable, not signed in): report it through
    /// <see cref="SharedProjectListResult.Failed"/> instead, so one broken source does not cost every other
    /// source's rows.
    /// </remarks>
    Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads enough of <paramref name="id"/>'s own portable definition to bind it to a local <c>Project</c>
    /// (AC-246) — called once, when the operator opens "Finish setting up…" on a row this source listed.
    /// </summary>
    /// <remarks>
    /// A one-time read, not a live handle: the host copies what comes back and does not call this again to keep
    /// it current. Must not throw for an ordinary failure — report it through
    /// <see cref="SharedProjectBindingResult.Failed"/> instead.
    /// </remarks>
    /// <param name="id">
    /// A <see cref="SharedProject.Id"/> this source itself listed — never one <see cref="ListAsync"/> never returned.
    /// </param>
    Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an edit to <paramref name="id"/>'s own claimed fields back to this source (AC-247) — called once,
    /// when the operator saves the project editor for an already-bound project.
    /// </summary>
    /// <remarks>
    /// Every field this source's own portable definition carries but <see cref="SharedProjectDefinitionEdit"/>
    /// does not mention must be carried through untouched. Must not throw for an ordinary failure — report it
    /// through <see cref="SharedProjectWriteBackResult.Failed"/>. Tell a permission failure and a checksum
    /// conflict apart wherever possible, per AC-247's "never silently overwrite" rule.
    /// </remarks>
    /// <param name="id">
    /// A <see cref="SharedProject.Id"/> this source itself listed.
    /// </param>
    /// <param name="edit">
    /// The operator's edited values.
    /// </param>
    /// <param name="baseChecksum">
    /// The <see cref="SharedProjectBinding.Checksum"/> from the read the operator's edit started from — sent
    /// unmodified, however long the editor stayed open, so this source's own optimistic-concurrency check answers
    /// the question it exists to answer: did anything change since the operator started editing, not since a
    /// moment ago.
    /// </param>
    Task<SharedProjectWriteBackResult> WriteBackAsync(string id, SharedProjectDefinitionEdit edit, string baseChecksum, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this source can turn a not-yet-shared local project into a new one of its own (AC-620). Required,
    /// not default-implemented.
    /// </summary>
    bool CanPublish { get; }

    /// <summary>
    /// The "Depot project" picker's own rows — unlike <see cref="ListAsync"/>, includes a target with no portable
    /// definition yet. Never called when <see cref="CanPublish"/> is false; report an ordinary failure through <see cref="SharedProjectPublishTargetListResult.Failed"/>.
    /// </summary>
    Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publishes <paramref name="definition"/> as a new portable definition at <paramref name="targetId"/> (AC-620) —
    /// must fail with <see cref="SharedProjectPublishResult.AlreadyPublished"/> rather than overwrite one that already exists (<see cref="PrepareBindingAsync"/>'s case, not this call's).
    /// </summary>
    /// <param name="targetId">
    /// A <see cref="SharedProjectPublishTarget.Id"/> this source itself listed.
    /// </param>
    /// <param name="definition">
    /// The local project's portable snapshot, offered as-is — see <see cref="SharedProjectPublishDefinition"/> for what "portable" already excludes.
    /// </param>
    Task<SharedProjectPublishResult> PublishAsync(string targetId, SharedProjectPublishDefinition definition, CancellationToken cancellationToken);
}
