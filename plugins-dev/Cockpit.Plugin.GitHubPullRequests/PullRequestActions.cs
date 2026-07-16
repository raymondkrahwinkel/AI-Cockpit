using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The two things you do to a pull request from either surface — drop its review prompt into the active session
/// (or the clipboard when there is none), and open it in the browser. Shared by the side-menu section and the
/// dashboard widget (#AC-18) so "click a PR" and "open a PR" mean exactly the same thing in both, down to the
/// toast they raise.
/// </summary>
internal static class PullRequestActions
{
    public static async Task InjectAsync(ICockpitHost host, GitHubPullRequestsSettings settings, GitHubPullRequest pullRequest)
    {
        var parts = pullRequest.Repository.Split('/', 2);
        var owner = parts.Length == 2 ? parts[0] : settings.Owner;
        var repo = parts.Length == 2 ? parts[1] : settings.Repo;
        var prompt = PromptTemplate.Render(settings.Template, pullRequest, owner, repo);

        if (host.Actions.HasActiveSession)
        {
            await host.Actions.InjectIntoActiveSessionAsync(prompt);
            host.ShowToast($"PR #{pullRequest.Number} added to the session's prompt.", PluginToastSeverity.Success);
        }
        else
        {
            await host.Actions.SetClipboardTextAsync(prompt);
            host.ShowToast($"No active session — PR #{pullRequest.Number}'s prompt copied to the clipboard.", PluginToastSeverity.Information);
        }
    }

    public static void OpenInBrowser(ICockpitHost host, string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            host.ShowToast($"Could not open the browser: {exception.Message}", PluginToastSeverity.Error);
        }
    }
}
