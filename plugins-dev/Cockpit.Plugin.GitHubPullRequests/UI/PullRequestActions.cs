using Cockpit.Plugin.GitHubPullRequests.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubPullRequests.UI;

// The two things you do to a pull request from either surface — drop its review prompt into the active session
// (or the clipboard when there is none), and open it in the browser. Shared by the side-menu section and the
// dashboard widget (#AC-18) so "click a PR" and "open a PR" mean exactly the same thing in both, down to the
// toast they raise.
//
// AC-1396: "the active session" is the one this window has selected, handed on by id.
internal static class PullRequestActions
{
    public static async Task InjectAsync(ICockpitUiHost host, GitHubPullRequestsSettings settings, GitHubPullRequest pullRequest)
    {
        var parts = pullRequest.Repository.Split('/', 2);
        var owner = parts.Length == 2 ? parts[0] : settings.Owner;
        var repo = parts.Length == 2 ? parts[1] : settings.Repo;
        var prompt = PromptTemplate.Render(settings.Template, pullRequest, owner, repo);

        if (host.ActivePaneId is { Length: > 0 } paneId)
        {
            await host.SendToSessionAsync(paneId, prompt);
            host.ShowToast($"PR #{pullRequest.Number} sent to the active session.", PluginToastSeverity.Success);
        }
        else
        {
            await host.SetClipboardTextAsync(prompt);
            host.ShowToast($"No active session — PR #{pullRequest.Number}'s prompt copied to the clipboard.", PluginToastSeverity.Information);
        }
    }

    public static void OpenInBrowser(ICockpitUiHost host, string? url)
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
