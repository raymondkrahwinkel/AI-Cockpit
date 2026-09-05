using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubActions;

// Reads recent GitHub Actions runs for the branch a repository is on, via the local GitHub CLI
// (`gh run list --branch &lt;branch&gt; --limit &lt;n&gt; --json …`) run in that repo's working directory — reusing the
// user's existing `gh` login, no token to paste. The branch comes from `git rev-parse` in the same
// directory. Fails soft: no gh, no login, no repo, a detached HEAD, or no runs yet all yield nothing
// rather than an error, so a session that has no CI simply shows nothing.
internal sealed class CiWorkflowRunClient
{
    // Whether a run URL is a safe https github.com link to hand to the OS browser opener. Internal for testing.
    internal static bool IsGitHubRunUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.Ordinal));

    // Opens a run's URL in the OS's default browser handler (never a shell string), shared by the header dot and
    // the dock panel's rows. Best effort: a non-GitHub url or a machine with no handler does nothing.
    public static void OpenRunInBrowser(string? url)
    {
        if (url is not { Length: > 0 } || !IsGitHubRunUrl(url))
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening a browser is a convenience — a machine without a handler just does nothing.
        }
    }

    // The gh arguments for the branch's most recent runs. Internal so a test can assert them without shelling out.
    // AC-1065: `updatedAt` joined createdAt so the dock panel can show a run's duration — the same call, one more
    // field, not a second API round trip.
    internal static string[] RunListArguments(string branch, int limit) =>
    [
        "run", "list", "--branch", branch, "--limit", limit.ToString(CultureInfo.InvariantCulture),
        "--json", "workflowName,headBranch,event,status,conclusion,createdAt,updatedAt,url",
    ];

    public async Task<CiRun?> GetLatestRunAsync(string workingDirectory, CancellationToken cancellationToken) =>
        (await GetRecentRunsAsync(workingDirectory, 1, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    // AC-1065: the same fetch as GetLatestRunAsync, for the dock panel's list rather than the header's single dot.
    public async Task<IReadOnlyList<CiRun>> GetRecentRunsAsync(string workingDirectory, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return [];
        }

        var branch = await _CurrentBranchAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(branch) || string.Equals(branch, "HEAD", StringComparison.Ordinal))
        {
            // No branch (not a repo) or a detached HEAD — nothing per-branch to show.
            return [];
        }

        var (exitCode, stdout, _) = await _RunAsync("gh", RunListArguments(branch, limit), workingDirectory, cancellationToken).ConfigureAwait(false);
        return exitCode != 0 ? [] : ParseRuns(stdout);
    }

    // Parses `gh run list --json …` output. Internal so a test can feed it a fixture.
    internal static IReadOnlyList<CiRun> ParseRuns(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<CiRun> runs = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                runs.Add(new CiRun(
                    _String(element, "workflowName"),
                    _String(element, "headBranch"),
                    _String(element, "event"),
                    _String(element, "status"),
                    _String(element, "conclusion"),
                    _DateTimeOffset(element, "createdAt"),
                    _String(element, "url"),
                    _DateTimeOffset(element, "updatedAt")));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return runs;
    }

    private static string _String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset? _DateTimeOffset(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
                ? parsed
                : null;

    private static async Task<string?> _CurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await _RunAsync("git", ["rev-parse", "--abbrev-ref", "HEAD"], workingDirectory, cancellationToken).ConfigureAwait(false);
        return exitCode == 0 ? stdout.Trim() : null;
    }

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

        // gh run list makes a network call and can stall; cap it so a hung request cannot pile up under the repeating
        // refresh timer, and cancel it when the caller (a detached header) goes away.
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
