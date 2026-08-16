using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Cockpit.Plugin.GitHubPullRequests;

// AC-818: PR status over MCP, so sessions waiting on the same PR don't each shell out to `gh pr view`. Wraps
// `GitHubPrGhClient.GetPullRequestStatusAsync` in a short-TTL cache: the in-flight task itself is stored, so a
// concurrent call for the same PR awaits it instead of starting its own.
//
// Question-driven, not a background poller — nothing runs until a caller actually asks about a PR.
internal sealed class GitHubPullRequestsMcpTools
{
    // Long enough that a handful of sessions polling the same PR a few seconds apart share one fetch; short
    // enough that "did the checks turn green yet" stays true within a coffee sip.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(25);
    private static readonly JsonSerializerOptions Serializer = new() { WriteIndented = false };

    private readonly GitHubPrGhClient _gh;

    // Keyed by "owner/repo#number". Static like GitHubPrGhClient's own caches: one cache per process, shared by
    // every session's MCP call — a per-instance cache would not coalesce anything.
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, (DateTimeOffset At, Task<string> Result)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public GitHubPullRequestsMcpTools(GitHubPrGhClient gh)
    {
        _gh = gh;
    }

    [McpServerTool(Name = "get_pr_status")]
    [Description("Returns a pull request's current status: title, url, checks (name/state/duration), mergeable (MERGEABLE/CONFLICTING/UNKNOWN) and review decision (APPROVED/CHANGES_REQUESTED/REVIEW_REQUIRED/null). Concurrent calls for the same PR within ~25s share one GitHub lookup instead of each hitting the API.")]
    public Task<string> GetPrStatus(
        [Description("Repository owner, e.g. 'acme'.")] string owner,
        [Description("Repository name, e.g. 'webshop'.")] string repo,
        [Description("Pull request number.")] int number)
    {
        var key = $"{owner}/{repo}#{number}";
        var now = DateTimeOffset.UtcNow;

        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var cached) && now - cached.At < Ttl)
            {
                return cached.Result;
            }

            // Detached from any one caller's cancellation on purpose — the fetch outlives the request that
            // started it, since other callers may still be waiting on the same task.
            var fetch = _FetchAsync(owner, repo, number);
            Cache[key] = (now, fetch);
            return fetch;
        }
    }

    // ponytail: a failed lookup is cached for the same TTL as a success, so a transient `gh` error blocks
    // retries for ~25s too. Give failures a shorter TTL if that turns out to bite in practice.
    private async Task<string> _FetchAsync(string owner, string repo, int number)
    {
        try
        {
            var status = await _gh.GetPullRequestStatusAsync(owner, repo, number, CancellationToken.None);
            if (status is null)
            {
                return _Fail($"No pull request #{number} found in {owner}/{repo} (or the 'gh' CLI is not available/authenticated).");
            }

            return JsonSerializer.Serialize(
                new
                {
                    ok = true,
                    number = status.Number,
                    title = status.Title,
                    url = status.Url,
                    mergeable = status.Mergeable,
                    reviewDecision = status.ReviewDecision,
                    checks = status.Checks.Select(check => new
                    {
                        name = check.Name,
                        state = check.State.ToString(),
                        durationSeconds = check.Duration?.TotalSeconds,
                    }),
                },
                Serializer);
        }
        catch (Exception exception)
        {
            return _Fail($"Could not fetch pull request status: {exception.Message}");
        }
    }

    private static string _Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, Serializer);
}
