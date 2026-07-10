using System.Diagnostics;
using System.Text.Json;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Lists open issues across all repositories for an owner via the local GitHub CLI (<c>gh search issues
/// --owner &lt;owner&gt; --state open --json …</c>), reusing the user's existing <c>gh</c> login — no token
/// to paste. Shelling out to a CLI the user already trusts keeps the plugin dependency-free.
/// </summary>
internal sealed class GitHubGhClient
{
    public async Task<IReadOnlyList<GitHubIssue>> SearchOpenIssuesAsync(string owner, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("search");
        startInfo.ArgumentList.Add("issues");
        startInfo.ArgumentList.Add("--owner");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(owner) ? "@me" : owner);
        startInfo.ArgumentList.Add("--state");
        startInfo.ArgumentList.Add("open");
        startInfo.ArgumentList.Add("--limit");
        startInfo.ArgumentList.Add("100");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("number,title,url,body,repository");

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

        return _Parse(stdout);
    }

    private static IReadOnlyList<GitHubIssue> _Parse(string json)
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
