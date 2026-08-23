namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// What came of <see cref="ISharedProjectSource.WriteBackAsync"/> (AC-247) — the write-side mirror of <see cref="SharedProjectListResult"/>/<see cref="SharedProjectBindingResult"/>'s own outcome idiom.
/// </summary>
public enum SharedProjectWriteBackOutcome
{
    /// <summary>
    /// The edit landed. <see cref="SharedProjectWriteBackResult.Checksum"/> is the new token for a follow-up write.
    /// </summary>
    Success,

    /// <summary>
    /// Someone else wrote first — the <c>baseChecksum</c> this write sent no longer matches the source's current
    /// copy. <see cref="SharedProjectWriteBackResult.LatestSnapshot"/> carries what the source found on a re-read
    /// done as part of answering this call, so a caller building a conflict view never has to read again just to
    /// show it. Never silently retried by the source itself — deciding what happens next is the caller's, by
    /// design (AC-247's own "nooit stil overschrijven" rule).
    /// </summary>
    ChecksumConflict,

    /// <summary>
    /// The operator's role does not allow writing here. Never retry with the same edit; show <see cref="SharedProjectWriteBackResult.Error"/> as the reason.
    /// </summary>
    PermissionDenied,

    /// <summary>
    /// Anything else — unreachable, malformed response, an error the source could not further classify.
    /// </summary>
    Failed,
}

/// <summary>
/// See <see cref="SharedProjectWriteBackOutcome"/>.
/// </summary>
public sealed record SharedProjectWriteBackResult(
    SharedProjectWriteBackOutcome Outcome, string? Checksum = null, string? Error = null, SharedProjectBinding? LatestSnapshot = null)
{
    public static SharedProjectWriteBackResult Success(string checksum) =>
        new(SharedProjectWriteBackOutcome.Success, Checksum: checksum);

    /// <param name="latest">
    /// A fresh read of the source's current state, taken while answering this call — never the caller's own stale edit.
    /// </param>
    public static SharedProjectWriteBackResult Conflict(SharedProjectBinding latest) =>
        new(SharedProjectWriteBackOutcome.ChecksumConflict, LatestSnapshot: latest);

    public static SharedProjectWriteBackResult PermissionDenied(string reason) =>
        new(SharedProjectWriteBackOutcome.PermissionDenied, Error: reason);

    public static SharedProjectWriteBackResult Failed(string error) =>
        new(SharedProjectWriteBackOutcome.Failed, Error: error);
}
