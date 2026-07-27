using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpAgentHeaders"/> (AC-354): which of the operator's own headers a spawned agent's config carries. The
/// precedence rule lives here rather than in each provider's config writer, because the three build their headers in
/// three different shapes and a rule resolved three times is a rule that drifts.
/// </summary>
public class McpAgentHeadersTests
{
    private static McpServerConfig _ServerWith(params McpHeader[] headers) => new()
    {
        Name = "private-api",
        Transport = McpTransport.Http,
        Url = "https://api.example/mcp",
        Headers = headers,
    };

    [Fact]
    public void For_CarriesTheOperatorsOwnHeaders()
    {
        var headers = McpAgentHeaders.For(_ServerWith(new McpHeader("X-Api-Key", "the-key")), bearerToken: null);

        Assert.Equal("the-key", headers["X-Api-Key"]);
    }

    [Fact]
    public void For_DropsAHalfWrittenRow()
    {
        var server = _ServerWith(new McpHeader("X-Api-Key", "  "), new McpHeader("  ", "orphan"));

        // A row the operator is still typing is not a header. Sending a blank field name gets a protocol error back
        // from some servers rather than anything that explains itself.
        Assert.Empty(McpAgentHeaders.For(server, bearerToken: null));
    }

    [Fact]
    public void For_WhenACredentialIsGoingOut_TheHandTypedAuthorizationIsDropped()
    {
        var server = _ServerWith(new McpHeader("Authorization", "Bearer stale-hand-typed"));

        var headers = McpAgentHeaders.For(server, bearerToken: "the-real-token");

        // Both would otherwise reach the provider, and which one won would depend on how that provider happens to
        // assemble its headers — a property of the agent rather than of what the operator configured.
        Assert.False(headers.ContainsKey("Authorization"));
    }

    [Fact]
    public void For_DropsAHandTypedAuthorization_RegardlessOfItsCasing()
    {
        var server = _ServerWith(new McpHeader("authorization", "Bearer stale-hand-typed"));

        // HTTP field names are case-insensitive, so "authorization" and "Authorization" are the same header; treating
        // them as two would let the dropped one back in under a different spelling.
        Assert.Empty(McpAgentHeaders.For(server, bearerToken: "the-real-token"));
    }

    [Fact]
    public void For_ForACockpitHostedEndpoint_DropsAHandTypedAuthorization()
    {
        var server = _ServerWith(new McpHeader("Authorization", "Bearer mine")) with { CockpitHosted = true };

        // A cockpit-hosted endpoint carries no literal token here — its auth rides an env var the provider
        // references — but it is still an Authorization the operator must not be able to displace.
        Assert.False(McpAgentHeaders.For(server, bearerToken: null).ContainsKey("Authorization"));
    }

    [Fact]
    public void For_WithNoCredential_LeavesAHandTypedAuthorizationAlone()
    {
        var server = _ServerWith(new McpHeader("Authorization", "Token abc123"));

        // Nothing is displacing it, and a server wanting a non-Bearer scheme on the standard header is exactly the
        // case this feature exists for.
        Assert.Equal("Token abc123", McpAgentHeaders.For(server, bearerToken: null)["Authorization"]);
    }

    [Fact]
    public void ToString_DoesNotPrintAHeaderValue()
    {
        var header = new McpHeader("X-Api-Key", "the-secret-value");

        // A custom header is where the credential goes for a server that does not take a bearer, so it falls under
        // the same rule as the bearer itself (Iron Law #8).
        var text = header.ToString();
        Assert.DoesNotContain("the-secret-value", text);
        Assert.Contains("X-Api-Key", text);
    }
}
