namespace Cockpit.Core.Mcp;

// AC-869: the registry/server name for the GitHub-pull-requests plugin's MCP endpoint, named here rather than
// read from the plugin itself — the same isolation reason as DelegationMcp. The literal is kept in sync by hand
// with GitHubPullRequestsPlugin's own AddMcpEndpoint call; there is no shared symbol across that boundary.
public static class GitHubPullRequestsMcp
{
    public const string ServerName = "cockpit-github-pull-requests";
}
