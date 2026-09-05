namespace Cockpit.Core.Depot;

public enum DepotMutationOutcome
{
    Success,
    Conflict,
    AuthorizationRequired,
    Failed,
}

// What came of a `move` or `delete` call (AC-280 criterion 5) — both share `write`'s optimistic `baseChecksum`
// contract, and a mismatch surfaces the same way: an ordinary failure whose text this client classifies (see
// DepotSyncClient._IsChecksumConflict), since Depot's MCP layer carries no separate, typed conflict signal.
public sealed record DepotMutationResult(DepotMutationOutcome Outcome, string? Error)
{
    public static DepotMutationResult Success { get; } = new(DepotMutationOutcome.Success, null);

    public static DepotMutationResult Conflict(string error) => new(DepotMutationOutcome.Conflict, error);

    public static DepotMutationResult AuthorizationRequired { get; } = new(DepotMutationOutcome.AuthorizationRequired, null);

    public static DepotMutationResult Failed(string error) => new(DepotMutationOutcome.Failed, error);
}
