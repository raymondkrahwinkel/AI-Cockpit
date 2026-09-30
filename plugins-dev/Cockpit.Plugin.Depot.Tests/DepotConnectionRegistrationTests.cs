using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotConnectionRegistration.McpServerName` (AC-243): the fixed `"Depot: "` prefix Raymond chose
// so a connection managed here can never collide with (and silently overwrite, since `AddMcpServer` is an
// upsert-by-name) an unrelated server an operator configured by hand in the MCP-servers dialog.
public class DepotConnectionRegistrationTests
{
}

// `PluginMcpSignInOutcome`'s zero value (AC-243): must be `Unavailable`, not `Authorized` —
// an unstubbed fake, a missed switch arm, or a deserialization gap all produce the default,
// none of which have actually signed anything in.
public class PluginMcpSignInOutcomeTests
{
    [Fact]
    public void DefaultValue_IsUnavailable_NeverAuthorized()
    {
        Assert.Equal(PluginMcpSignInOutcome.Unavailable, default(PluginMcpSignInOutcome));
    }
}
