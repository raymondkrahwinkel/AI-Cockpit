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
}
