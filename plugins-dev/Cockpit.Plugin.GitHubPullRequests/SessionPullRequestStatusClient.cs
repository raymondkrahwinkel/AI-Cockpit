using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubPullRequests;

// AC-802: one `gh pr view` call for the session's checked-out branch; fails soft exactly like
// Cockpit.Plugin.GitHubActions.CiWorkflowRunClient (no gh/repo/PR/timeout all yield `null`).
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
                    checks.Add(PullRequestCheckRollupParser.Parse(element));
                }
            }

            return new SessionPullRequestStatus(number, repository, branch, additions, deletions, url, checks);
        }
        catch (JsonException)
        {
            return null;
        }
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
