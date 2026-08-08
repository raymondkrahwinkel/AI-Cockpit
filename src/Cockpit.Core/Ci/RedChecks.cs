using System.Text.Json;

namespace Cockpit.Core.Ci;

// One check on a pull request, as `gh pr checks --json bucket,name,workflow,link` reports it. `bucket` is gh's own
// normalisation of the many per-provider states down to pass/fail/pending/skipping/cancel.
public sealed record CiCheck(string Name, string Workflow, string Bucket, string Link)
{
    public bool IsRed => string.Equals(Bucket, "fail", StringComparison.OrdinalIgnoreCase);

    // A skipped check is a check that was never going to run, not one still to come — it blocks nothing.
    public bool IsGreen =>
        string.Equals(Bucket, "pass", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Bucket, "skipping", StringComparison.OrdinalIgnoreCase);
}

// What `gh pr view --json reviewDecision,mergeable` said about the merge itself (AC-645) — the half `gh pr checks`
// does not answer. `reviewDecision` is empty when the repository requires no review.
public sealed record PrMergeState(string ReviewDecision, string Mergeable)
{
    public bool IsReadyToMerge =>
        string.Equals(Mergeable, "MERGEABLE", StringComparison.OrdinalIgnoreCase)
        && (ReviewDecision.Length == 0 || string.Equals(ReviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase));
}

// What `gh pr checks` said, and which of it is news (AC-634). Kept apart from the watcher that runs gh so the
// judgement — the half that can be wrong — is testable without a process.
public static class RedChecks
{
    // The checks in gh's JSON. Anything unparseable is no checks: a watcher that guessed at a malformed answer would
    // raise an alarm about a repository it could not read.
    public static IReadOnlyList<CiCheck> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. document.RootElement.EnumerateArray().Select(element => new CiCheck(
                _String(element, "name"),
                _String(element, "workflow"),
                _String(element, "bucket"),
                _String(element, "link")))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // The red checks that were not already red when this checkout was last looked at. Red stays red for as long as it
    // takes to fix, and a watcher that told you every five minutes is a watcher you turn off.
    public static IReadOnlyList<CiCheck> NewlyRed(IReadOnlyList<CiCheck> checks, IReadOnlySet<string> alreadyReported) =>
        [.. checks.Where(check => check.IsRed && !alreadyReported.Contains(check.Name))];

    // The names to remember as reported. A check that goes green and later fails again is news a second time, so this
    // replaces what was held rather than adding to it.
    public static IReadOnlySet<string> RedNames(IReadOnlyList<CiCheck> checks) =>
        checks.Where(check => check.IsRed).Select(check => check.Name).ToHashSet(StringComparer.Ordinal);

    // AC-645: every check in and none of them red or still running. No checks at all is not green — it is a pull
    // request whose CI has not started, or a directory gh could not read, and neither is something to call ready.
    public static bool AllGreen(IReadOnlyList<CiCheck> checks) =>
        checks.Count > 0 && checks.All(check => check.IsGreen);

    // What `gh pr view` said about the merge. Unparseable is "nothing is known", which reads as not ready — the
    // conservative way round, since the cost of a missed report is a nudge that comes five minutes later.
    public static PrMergeState ParseMergeState(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PrMergeState(string.Empty, string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind != JsonValueKind.Object
                ? new PrMergeState(string.Empty, string.Empty)
                : new PrMergeState(_String(document.RootElement, "reviewDecision"), _String(document.RootElement, "mergeable"));
        }
        catch (JsonException)
        {
            return new PrMergeState(string.Empty, string.Empty);
        }
    }

    private static string _String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
