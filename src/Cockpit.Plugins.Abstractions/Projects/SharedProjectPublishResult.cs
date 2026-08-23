namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What came of <see cref="ISharedProjectSource.PublishAsync"/> (AC-620) — the first-publish mirror of <see cref="SharedProjectWriteBackOutcome"/>'s own idiom.
/// </summary>
public enum SharedProjectPublishOutcome
{
    /// <summary>
    /// The project is now published. <see cref="SharedProjectPublishResult.BoundId"/> is the reference to bind the local project to, same shape as <see cref="SharedProject.Id"/>.
    /// </summary>
    Success,

    /// <summary>
    /// The target already carries a portable definition — this is an "attach" (<see cref="ISharedProjectSource.PrepareBindingAsync"/>),
    /// not a publish, and this call is never the one that overwrites it. Never retried with the same target.
    /// </summary>
    AlreadyPublished,

    /// <summary>
    /// The operator's role on the target does not allow publishing there. Never retry; show <see cref="SharedProjectPublishResult.Error"/> as the reason.
    /// </summary>
    PermissionDenied,

    /// <summary>
    /// Anything else — unreachable, not signed in, a malformed response.
    /// </summary>
    Failed,
}

/// <summary>
/// See <see cref="SharedProjectPublishOutcome"/>.
/// </summary>
public sealed record SharedProjectPublishResult(SharedProjectPublishOutcome Outcome, string? BoundId = null, string? Error = null)
{
    public static SharedProjectPublishResult Success(string boundId) => new(SharedProjectPublishOutcome.Success, BoundId: boundId);

    public static SharedProjectPublishResult AlreadyPublished(string reason) => new(SharedProjectPublishOutcome.AlreadyPublished, Error: reason);

    public static SharedProjectPublishResult PermissionDenied(string reason) => new(SharedProjectPublishOutcome.PermissionDenied, Error: reason);

    public static SharedProjectPublishResult Failed(string error) => new(SharedProjectPublishOutcome.Failed, Error: error);
}
