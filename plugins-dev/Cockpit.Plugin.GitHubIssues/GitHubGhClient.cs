using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Lists open issues across all repositories for an owner via the local GitHub CLI (<c>gh search issues
/// --owner &lt;owner&gt; --state open --json …</c>), reusing the user's existing <c>gh</c> login — no token
/// to paste. Issues in archived repositories are excluded (the search API returns them, but they are
/// resolved and filtered against <c>gh repo list --archived</c>). Shelling out to a CLI the user already
/// trusts keeps the plugin dependency-free.
/// </summary>
internal sealed class GitHubGhClient
{
    public async Task<IReadOnlyList<GitHubIssue>> SearchOpenIssuesAsync(string owner, CancellationToken cancellationToken)
    {
        var normalizedOwner = string.IsNullOrWhiteSpace(owner) ? "@me" : owner.Trim();
        var archived = await _GetArchivedReposAsync(normalizedOwner, cancellationToken);

        var searchArgs = new[]
        {
            "search", "issues", "--owner", normalizedOwner, "--state", "open",
            "--limit", "100", "--json", "number,title,url,body,repository",
        };
        var issues = _ParseIssues(await _RunGhAsync(searchArgs, cancellationToken));

        return archived.Count == 0
            ? issues
            : issues.Where(issue => !archived.Contains(issue.Repository)).ToList();
    }

    // The archived repos for the owner; "@me"/blank means the current gh user (no owner argument).
    private static async Task<HashSet<string>> _GetArchivedReposAsync(string owner, CancellationToken cancellationToken)
    {
        var args = new List<string> { "repo", "list" };
        if (!string.Equals(owner, "@me", StringComparison.OrdinalIgnoreCase))
        {
            args.Add(owner);
        }

        args.AddRange(["--archived", "--limit", "1000", "--json", "nameWithOwner"]);

        try
        {
            var json = await _RunGhAsync(args.ToArray(), cancellationToken);
            using var document = JsonDocument.Parse(json);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("nameWithOwner", out var nwo) && nwo.GetString() is { } name)
                {
                    result.Add(name);
                }
            }

            return result;
        }
        catch
        {
            // If the archived list can't be fetched, fail open (show everything) rather than hiding issues.
            return [];
        }
    }

    private static async Task<string> _RunGhAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
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
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Could not run 'gh' — is the GitHub CLI installed and on PATH? ({exception.Message})", exception);
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"gh exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }

    private static IReadOnlyList<GitHubIssue> _ParseIssues(string json)
    {
        using var document = JsonDocument.Parse(json);
        var issues = new List<GitHubIssue>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var number = element.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
            var title = element.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var url = element.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var body = element.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var repository = element.TryGetProperty("repository", out var repo) && repo.TryGetProperty("nameWithOwner", out var nwo)
                ? nwo.GetString() ?? string.Empty
                : string.Empty;
            issues.Add(new GitHubIssue(number, title, url, body, repository));
        }

        return issues;
    }
}
