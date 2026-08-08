using System.Text.Json;

namespace Cockpit.Core.Ci;

// One check on a pull request, as `gh pr checks --json bucket,name,workflow,link` reports it. `bucket` is gh's own
// normalisation of the many per-provider states down to pass/fail/pending/skipping/cancel.
public sealed record CiCheck(string Name, string Workflow, string Bucket, string Link)
{
    public bool IsRed => string.Equals(Bucket, "fail", StringComparison.OrdinalIgnoreCase);
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

    private static string _String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
