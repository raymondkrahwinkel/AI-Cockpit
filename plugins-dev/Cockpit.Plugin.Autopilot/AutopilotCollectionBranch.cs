namespace Cockpit.Plugin.Autopilot;

// The collection-branch name an epic run's sub lands on (AC-1337, D2): a name derived from the epic itself, so a
// run needs no branch typed in by hand. `directToMain` is the operator's explicit opt-out back to v1 (PR per sub
// straight to main); a run with no epic (a single-issue click) has nothing to derive a branch from either way.
internal static class AutopilotCollectionBranch
{
    public static string? For(bool directToMain, string? epicId) =>
        directToMain || string.IsNullOrWhiteSpace(epicId) ? null : $"epic/{epicId.ToLowerInvariant()}";
}
