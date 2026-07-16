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
        Auth = ToAuth(contribution.BearerToken),
        ApiKey = contribution.BearerToken,
    };

    public static McpServerAuth ToAuth(string? bearerToken) =>
        string.IsNullOrEmpty(bearerToken) ? McpServerAuth.None : McpServerAuth.ApiKey;

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
