using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubPullRequests;

// Reads the open pull request for the branch a session's own working directory is checked out on (AC-802), via
// one `gh pr view --json number,headRefName,additions,deletions,url,statusCheckRollup` call run in that
// directory — everything the session banner needs, collapsed and expanded, in a single round trip. Mirrors
// Cockpit.Plugin.GitHubActions.CiWorkflowRunClient's fail-soft contract and timeout handling: no gh, no login, no
// repo, a detached HEAD, or no open PR for the branch all yield `null` rather than an error, so a session with
// nothing to show simply shows nothing.
internal sealed class SessionPullRequestStatusClient
{
    // The gh arguments for the current checkout's open pull request. Internal so a test can assert them without
    // shelling out.
    internal static readonly string[] ViewArguments =
    [
        "pr", "view", "--json", "number,headRefName,additions,deletions,url,statusCheckRollup",
    ];

    public async Task<SessionPullRequestStatus?> GetOpenPullRequestAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var (exitCode, stdout, _) = await _RunAsync("gh", ViewArguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        return exitCode != 0 ? null : Parse(stdout);
    }

    // Parses `gh pr view --json …` output. Internal so a test can feed it a fixture, the same seam
    // CiWorkflowRunClient.ParseRuns gives its own JSON.
    internal static SessionPullRequestStatus? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var number = root.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
            var branch = _String(root, "headRefName");
            var additions = root.TryGetProperty("additions", out var a) ? a.GetInt32() : 0;
            var deletions = root.TryGetProperty("deletions", out var d) ? d.GetInt32() : 0;
            var url = _String(root, "url");
            var repository = _RepositoryFromUrl(url);

            var checks = new List<PullRequestCheck>();
            if (root.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in rollup.EnumerateArray())
                {
                    checks.Add(_ParseCheck(element));
                }
            }

            return new SessionPullRequestStatus(number, repository, branch, additions, deletions, url, checks);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PullRequestCheck _ParseCheck(JsonElement element)
    {
        var name = element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? string.Empty
            : _String(element, "context");

        var typename = element.TryGetProperty("__typename", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var status = element.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var conclusion = element.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var state = element.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;

        TimeSpan? duration = null;
        if (element.TryGetProperty("startedAt", out var startedEl) && startedEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(startedEl.GetString(), out var started)
            && element.TryGetProperty("completedAt", out var completedEl) && completedEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(completedEl.GetString(), out var completed))
        {
            duration = completed - started;
        }

        return new PullRequestCheck(name, _DeriveState(typename, status, conclusion, state), duration);
    }

    // A CheckRun (a GitHub Actions job) reports status (QUEUED/IN_PROGRESS/COMPLETED) and, once completed, a
    // conclusion — the same status/conclusion split CiRun.State reads. A StatusContext (a legacy commit status,
    // e.g. from an external CI) has no separate status: state alone (PENDING/SUCCESS/FAILURE/ERROR) carries both.
    private static PullRequestCheckState _DeriveState(string? typename, string? status, string? conclusion, string? state)
    {
        if (string.Equals(typename, "StatusContext", StringComparison.OrdinalIgnoreCase) || status is null)
        {
            return state?.ToUpperInvariant() switch
            {
                "SUCCESS" => PullRequestCheckState.Passed,
                "FAILURE" or "ERROR" => PullRequestCheckState.Failed,
                "PENDING" or "EXPECTED" => PullRequestCheckState.Running,
                _ => PullRequestCheckState.Other,
            };
        }

        if (!string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return PullRequestCheckState.Running;
        }

        return conclusion?.ToUpperInvariant() switch
        {
            "SUCCESS" => PullRequestCheckState.Passed,
            "FAILURE" or "TIMED_OUT" or "STARTUP_FAILURE" => PullRequestCheckState.Failed,
            _ => PullRequestCheckState.Other,
        };
    }

    // A PR's own URL already carries owner/repo (https://github.com/{owner}/{repo}/pull/{number}) — one fewer
    // field to ask `gh pr view` for.
    private static string _RepositoryFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Segments.Length >= 3
            ? $"{uri.Segments[1].TrimEnd('/')}/{uri.Segments[2].TrimEnd('/')}"
            : string.Empty;

    private static string _String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static async Task<(int ExitCode, string StdOut, string StdErr)> _RunAsync(string executable, string[] arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception)
        {
            // The executable is not installed / not on PATH — fail soft (the caller shows nothing).
            return (-1, string.Empty, string.Empty);
        }

        // gh pr view makes a network call and can stall; cap it so a hung request cannot pile up under the
        // repeating refresh timer, and cancel it when the caller (a detached banner) goes away — same discipline
        // as CiWorkflowRunClient._RunAsync.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Drain both streams concurrently — reading one to end before the other can deadlock if the child fills
            // the other pipe's buffer.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Timed out or the caller cancelled — kill the stuck process so it cannot accumulate, and show nothing.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Best effort — the process may have exited between the check and the kill.
            }

            return (-1, string.Empty, string.Empty);
        }
    }
}
