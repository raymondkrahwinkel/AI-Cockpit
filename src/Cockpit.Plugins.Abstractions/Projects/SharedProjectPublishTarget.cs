namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// A place on an <see cref="ISharedProjectSource"/> the operator could publish a not-yet-shared local project into
/// (AC-620) — what <see cref="ISharedProjectSource.ListPublishTargetsAsync"/> offers for the "Depot project" picker.
/// Distinct from <see cref="SharedProject"/>: that lists projects the source already shares a portable definition
/// for, this lists containers the operator could write a first definition into, whether or not one exists there yet
/// (<see cref="ISharedProjectSource.PublishAsync"/> is what tells the two apart, at write time).
/// </summary>
/// <param name="Id">
/// The reference a local project would carry once published — same shape as <see cref="SharedProject.Id"/>
/// (<c>&lt;scheme&gt;:&lt;value&gt;</c>), so a successful publish can bind the local project the same way
/// <see cref="ISharedProjectSource.PrepareBindingAsync"/>'s own result does.
/// </param>
/// <param name="Name">
/// The target's display name, read from wherever the source keeps its own listing.
/// </param>
/// <param name="Role">
/// The operator's membership role there, same idiom as <see cref="SharedProject.Role"/> — display only.
/// </param>
public sealed record SharedProjectPublishTarget(string Id, string Name, string? Role);
