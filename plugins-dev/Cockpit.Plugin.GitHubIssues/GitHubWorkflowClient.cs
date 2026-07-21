using System.Diagnostics;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The writing half of the GitHub CLI (#77) — assigning, labelling, commenting, closing. The reading half
/// (<see cref="GitHubGhClient"/>) searches; this one changes things, which is why nothing here is cached and every
/// failure is raised rather than swallowed: a flow that reports "assigned" for an assignment GitHub refused is worse
/// than one that stops.
/// <para>
/// It goes through <c>gh</c>, like the rest of this plugin, so it reuses the login the operator already has. No token
/// to paste, and no scope this cockpit has to ask for.
/// </para>
/// </summary>
internal sealed class GitHubWorkflowClient
{
    public async Task<GitHubIssue> GetIssueAsync(GitHubIssueReference issue, CancellationToken cancellationToken)
    {
        var json = await _RunAsync(
            ["issue", "view", issue.Number.ToString(), "--repo", issue.Repository, "--json", "number,title,url,body"],
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new GitHubIssue(
            root.GetProperty("number").GetInt32(),
            root.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null,
            issue.Repository);
    }

    /// <summary>Reads an issue's comments (<c>gh issue view --json comments</c>), normalized to <see cref="TrackerComment"/> (GitHub's <c>createdAt</c> is an ISO-8601 string).</summary>
    public async Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(GitHubIssueReference issue, CancellationToken cancellationToken)
    {
        var json = await _RunAsync(
            ["issue", "view", issue.Number.ToString(), "--repo", issue.Repository, "--json", "comments"],
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        var comments = new List<TrackerComment>();
        if (!document.RootElement.TryGetProperty("comments", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return comments;
        }

        foreach (var comment in array.EnumerateArray())
        {
            var login = comment.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object && author.TryGetProperty("login", out var loginValue)
                ? loginValue.GetString() ?? string.Empty
                : string.Empty;
            var body = comment.TryGetProperty("body", out var bodyValue) && bodyValue.ValueKind == JsonValueKind.String ? bodyValue.GetString() ?? string.Empty : string.Empty;
            var created = comment.TryGetProperty("createdAt", out var createdValue) && createdValue.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(createdValue.GetString(), out var timestamp)
                ? timestamp
                : DateTimeOffset.MinValue;
            comments.Add(new TrackerComment(login, body, created));
        }

        return comments;
    }

    public Task AssignToMeAsync(GitHubIssueReference issue, CancellationToken cancellationToken) =>
        _RunAsync(
            ["issue", "edit", issue.Number.ToString(), "--repo", issue.Repository, "--add-assignee", "@me"],
            cancellationToken);

    /// <summary>Puts a label on the issue. A label the repo does not have fails here, and says which ones it does have — an automation that invents a label is how a repo ends up with three that mean the same thing.</summary>
    public async Task AddLabelAsync(GitHubIssueReference issue, string label, CancellationToken cancellationToken)
    {
        try
        {
            await _RunAsync(
                ["issue", "edit", issue.Number.ToString(), "--repo", issue.Repository, "--add-label", label],
                cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            var labels = await _LabelsAsync(issue.Repository, cancellationToken);

            throw new InvalidOperationException(
                labels.Count == 0
                    ? $"{issue.Repository} has no label called '{label}', and no labels this token can see."
                    : $"{issue.Repository} has no label called '{label}'. It has: {string.Join(", ", labels)}.");
        }
    }

    public Task CommentAsync(GitHubIssueReference issue, string comment, CancellationToken cancellationToken) =>
        _RunAsync(
            ["issue", "comment", issue.Number.ToString(), "--repo", issue.Repository, "--body", comment],
            cancellationToken);

    public Task CloseAsync(GitHubIssueReference issue, string reason, string comment, CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "issue", "close", issue.Number.ToString(), "--repo", issue.Repository, "--reason", reason,
        };

        if (comment.Length > 0)
        {
            arguments.Add("--comment");
            arguments.Add(comment);
        }

        return _RunAsync([.. arguments], cancellationToken);
    }

    private async Task<IReadOnlyList<string>> _LabelsAsync(string repository, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _RunAsync(["label", "list", "--repo", repository, "--json", "name", "--limit", "60"], cancellationToken);

            using var document = JsonDocument.Parse(json);

            return document.RootElement
                .EnumerateArray()
                .Select(label => label.GetProperty("name").GetString() ?? string.Empty)
                .Where(name => name.Length > 0)
                .ToList();
        }
        catch (InvalidOperationException)
        {
            // The list is a courtesy in an error message. Failing to fetch it must not replace the real failure.
            return [];
        }
    }

    private static async Task<string> _RunAsync(string[] arguments, CancellationToken cancellationToken)
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
            throw new InvalidOperationException(stderr.Trim() is { Length: > 0 } said
                ? said
                : $"gh exited with {process.ExitCode}.");
        }

        return stdout;
    }
}
