namespace Cockpit.Core.Depot;

public enum DepotPushOutcome
{
    Success,
    Failed,
}

// The outcome of one push of a Depot mirror (AC-282). Only `Pushed` had its base/index refreshed — the other
// three are write_many's own per-file statuses (AC-280) and never touch local state, which is what keeps a
// Diverged or Retained file (AC-281) from being silently pushed: it just surfaces as an ordinary conflict.
public sealed record DepotPushResult(
    DepotPushOutcome Outcome,
    IReadOnlyList<string> Pushed,
    IReadOnlyList<string> Conflicted,
    IReadOnlyList<string> Invalid,
    IReadOnlyList<string> Unwritten,
    string? Error)
{
    public static DepotPushResult Success(
        IReadOnlyList<string> pushed, IReadOnlyList<string> conflicted, IReadOnlyList<string> invalid, IReadOnlyList<string> unwritten) =>
        new(DepotPushOutcome.Success, pushed, conflicted, invalid, unwritten, null);

    public static DepotPushResult Failed(string error) => new(DepotPushOutcome.Failed, [], [], [], [], error);
}
