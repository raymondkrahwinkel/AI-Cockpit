using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.App.Plugins;

/// <summary>
/// Maps a plugin's <see cref="McpServerContribution"/> (a plugin-ALC-safe DTO, no <c>Cockpit.Core</c> types) to
/// the host's own <see cref="McpServerConfig"/> (#60, AC-11). The two sit either side of the plugin isolation
/// boundary and are declared independently, so the mapping lives here — the one place that sees both — and is
/// shared by the pull path (<see cref="McpServerCatalog"/>) and the legacy push path
/// (<see cref="CockpitHost.AddMcpServer"/>).
/// </summary>
internal static class PluginMcpMapping
{
    public static McpServerConfig ToServerConfig(McpServerContribution contribution) => new()
    {
        Name = contribution.Name,
        Transport = McpTransport.Http,
        Scope = ToServerScope(contribution.Scope),
        Url = contribution.Url,
        Auth = ToAuth(contribution),
        ApiKey = contribution.BearerToken,
        OAuthAuthority = contribution.OAuthAuthority,
        OAuthClientId = contribution.OAuthClientId,
    };

    // A non-empty OAuthAuthority is the contribution's only way to say "this is OAuth" (AC-500) — the DTO has no
    // Cockpit.Core McpServerAuth to set directly, by the same isolation rule ToServerConfig's doc comment names.
    // Checked ahead of BearerToken so a contribution that (wrongly) sets both is still treated as OAuth rather than
    // silently degraded to a static token that will never satisfy the server's real auth requirement.
    public static McpServerAuth ToAuth(McpServerContribution contribution) => contribution switch
    {
        { OAuthAuthority.Length: > 0 } => McpServerAuth.OAuth,
        { BearerToken.Length: > 0 } => McpServerAuth.ApiKey,
        _ => McpServerAuth.None,
    };

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
