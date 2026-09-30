using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpOAuthAuthorizer"/> (AC-353): the two things about the options that the rest of the feature rests on
/// — that the token is stored somewhere the cockpit can reach it afterwards, and that a caller who is not the
/// operator cannot make a browser window appear.
/// </summary>
public class McpOAuthAuthorizerTests
{
    private static readonly McpServerConfig Server = new()
    {
        Id = "depot",
        Name = "depot",
        Transport = McpTransport.Http,
        Url = "https://depot.example/mcp",
        Auth = McpServerAuth.OAuth,
    };

    private static readonly Uri AuthorizationUri = new("https://depot.example/connect/authorize");

    /// <summary>A scheme the real hand-off refuses to give the shell, so nothing can take it.</summary>
    private static readonly Uri NonBrowsableAuthorizationUri = new("cockpit-test:authorize");

    private static McpOAuthAuthorizer _Create(FakeMcpOAuthTokenStore store) =>
        new(NullLogger<McpOAuthAuthorizer>.Instance, store);

    [Fact]
    public void CreateOptions_WithConfiguredOAuthScopes_SetsAScopeSelectorThatReplacesWhateverWasDerived()
    {
        var server = Server with { OAuthScopes = "depot custom-scope" };

        var options = _Create(new FakeMcpOAuthTokenStore()).CreateOptions(server);

        // ClientOAuthOptions.Scopes is only ever a fallback the SDK falls through to when a server gave it nothing
        // to derive from — it cannot override a server that advertises its own scopes_supported. ScopeSelector runs
        // after that derivation and replaces it outright, which is what a per-server override (AC-505 criterion 3)
        // actually needs. Fed an unrelated candidate list (standing in for whatever the SDK derived), it must still
        // come back with exactly the configured scopes.
        Assert.NotNull(options.ScopeSelector);
        var selected = options.ScopeSelector(["openid", "offline_access", "depot"]);
        Assert.Equal(["depot", "custom-scope"], selected);
    }

    [Fact]
    public void CreateOptions_WithCommaSeparatedOAuthScopes_SplitsThemAnyway()
    {
        // The field is free text; a scope list pasted from a server's own docs is at least as often
        // comma-separated as space-separated.
        var server = Server with { OAuthScopes = "openid, offline_access,\tdepot" };

        var options = _Create(new FakeMcpOAuthTokenStore()).CreateOptions(server);

        Assert.NotNull(options.ScopeSelector);
        Assert.Equal(["openid", "offline_access", "depot"], options.ScopeSelector([]));
    }

}
