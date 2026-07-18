using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubActions;

/// <summary>
/// Reads the latest GitHub Actions run for the branch a repository is on, via the local GitHub CLI
/// (<c>gh run list --branch &lt;branch&gt; --limit 1 --json …</c>) run in that repo's working directory — reusing the
/// user's existing <c>gh</c> login, no token to paste. The branch comes from <c>git rev-parse</c> in the same
/// directory. Fails soft: no gh, no login, no repo, a detached HEAD, or no runs yet all yield <see langword="null"/>
/// rather than an error, so a session that has no CI simply shows nothing.
/// </summary>
internal sealed class CiWorkflowRunClient
{
    /// <summary>Whether a run URL is a safe https github.com link to hand to the OS browser opener. Internal for testing.</summary>
    internal static bool IsGitHubRunUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.Ordinal));

    /// <summary>The gh arguments for the latest run on a branch. Internal so a test can assert them without shelling out.</summary>
    internal static string[] RunListArguments(string branch) =>
    [
        "run", "list", "--branch", branch, "--limit", "1",
        "--json", "workflowName,headBranch,event,status,conclusion,createdAt,url",
    ];

    public async Task<CiRun?> GetLatestRunAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var branch = await _CurrentBranchAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(branch) || string.Equals(branch, "HEAD", StringComparison.Ordinal))
        {
            // No branch (not a repo) or a detached HEAD — nothing per-branch to show.
            return null;
        }

        var (exitCode, stdout, _) = await _RunAsync("gh", RunListArguments(branch), workingDirectory, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return null;
        }

        return ParseRuns(stdout).FirstOrDefault();
    }

    /// <summary>Parses <c>gh run list --json …</c> output. Internal so a test can feed it a fixture.</summary>
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
                var createdAt = element.TryGetProperty("createdAt", out var created)
                    && created.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(created.GetString(), out var parsed)
                        ? parsed
                        : (DateTimeOffset?)null;

                runs.Add(new CiRun(
                    _String(element, "workflowName"),
                    _String(element, "headBranch"),
                    _String(element, "event"),
                    _String(element, "status"),
                    _String(element, "conclusion"),
                    createdAt,
                    _String(element, "url")));
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
