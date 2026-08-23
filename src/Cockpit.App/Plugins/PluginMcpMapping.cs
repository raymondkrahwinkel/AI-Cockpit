using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.App.Plugins;

// Maps a plugin's `McpServerContribution` (plugin-ALC-safe DTO) to the host's `McpServerConfig`
// (#60, AC-11) — the one place that sees both sides of the plugin isolation boundary, shared by the
// pull path (`McpServerCatalog`) and the legacy push path (`CockpitHost.AddMcpServer`).
internal static class PluginMcpMapping
{
    public static McpServerConfig ToServerConfig(McpServerContribution contribution)
    {
        var auth = ToAuth(contribution);
        return new McpServerConfig
        {
            Name = contribution.Name,
            // The plugin's own stable id when it offers one (AC-403), so a connection it renames keeps the token
            // filed under it. A plugin that offers none keeps exactly the behaviour it had: the id the name derives
            // to, which is the key the name-keyed store used before this existed.
            Id = string.IsNullOrWhiteSpace(contribution.Id)
                ? McpServerIdentity.LegacyIdFor(contribution.Name)
                : contribution.Id.Trim(),
            Transport = McpTransport.Http,
            Scope = ToServerScope(contribution.Scope),
            Url = contribution.Url,
            Auth = auth,
            // Only the field the resolved auth actually uses is kept — the same rule the MCP-servers dialog's own
            // ToConfig() applies — so a contribution that (wrongly) set both a token and an authority never leaves
            // a dead, unused secret sitting in the registry beside the OAuth config that is actually in effect.
            ApiKey = auth == McpServerAuth.ApiKey ? contribution.BearerToken : null,
            OAuthAuthority = auth == McpServerAuth.OAuth ? contribution.OAuthAuthority!.Trim() : null,
            OAuthClientId = auth == McpServerAuth.OAuth ? contribution.OAuthClientId : null,
        };
    }

    // A non-empty OAuthAuthority is the contribution's only way to say "this is OAuth" (AC-500), checked
    // ahead of BearerToken so a contribution setting both is still treated as OAuth, not a dead static token.
    public static McpServerAuth ToAuth(McpServerContribution contribution) =>
        !string.IsNullOrWhiteSpace(contribution.OAuthAuthority) ? McpServerAuth.OAuth
        : !string.IsNullOrEmpty(contribution.BearerToken) ? McpServerAuth.ApiKey
        : McpServerAuth.None;

    // Mapped by name, not ordinal — McpContributionScope and McpServerScope are declared independently (isolation,
    // see the ICockpitHost doc comment) and are free to diverge in order.
    public static McpServerScope ToServerScope(McpContributionScope scope) => scope switch
    {
        McpContributionScope.All => McpServerScope.All,
        McpContributionScope.LocalOnly => McpServerScope.LocalOnly,
        McpContributionScope.ClaudeOnly => McpServerScope.ClaudeOnly,
        _ => McpServerScope.All,
    };
}
