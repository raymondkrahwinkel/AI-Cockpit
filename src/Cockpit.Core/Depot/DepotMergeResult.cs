namespace Cockpit.Core.Depot;

// One diverged file (AC-281) this merge round could not resolve without a human — a git merge-file conflict, or
// this ticket's refusal to guess at an unconfirmed base, binary content, or a side that disappeared. Either way
// the shadow base/index for this path are exactly as they were before the round started.
public sealed record DepotMergeConflict(string Path, string Reason);

public enum DepotMergeOutcome
{
    Success,
    AuthorizationRequired,
    Failed,
}

// The outcome of one 3-way merge round over AC-281's diverged files (AC-283). `Merged` had a clean git
// merge-file result written and its shadow base/index re-based onto Depot's current checksum, so the next
// ordinary push (AC-282) lands it; `Conflicted` had its base/index left completely untouched instead.
public sealed record DepotMergeResult(
    DepotMergeOutcome Outcome,
    IReadOnlyList<string> Merged,
    IReadOnlyList<DepotMergeConflict> Conflicted,
    string? Error)
{
    public static DepotMergeResult Success(IReadOnlyList<string> merged, IReadOnlyList<DepotMergeConflict> conflicted) =>
        new(DepotMergeOutcome.Success, merged, conflicted, null);

    public static DepotMergeResult AuthorizationRequired { get; } = new(DepotMergeOutcome.AuthorizationRequired, [], [], null);

    public static DepotMergeResult Failed(string error) => new(DepotMergeOutcome.Failed, [], [], error);
}
