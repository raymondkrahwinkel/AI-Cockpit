using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugin.Depot.Model;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotConnectionRegistration.McpServerName` (AC-243): the fixed `"Depot: "` prefix Raymond chose
// so a connection managed here can never collide with (and silently overwrite, since `AddMcpServer` is an
// upsert-by-name) an unrelated server an operator configured by hand in the MCP-servers dialog.
public class DepotConnectionRegistrationTests
{
    [Fact]
    public void McpServerName_IsThePrefixedName()
    {
        var connection = new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com");

        Assert.Equal("Depot: Work", connection.McpServerName);
    }

    [Fact]
    public void McpServerName_TwoConnectionsWithDifferentNames_ContributeUnderDifferentNames()
    {
        var first = new DepotConnectionRegistration("conn-1", "Work", "https://a.example.com");
        var second = new DepotConnectionRegistration("conn-2", "Personal", "https://b.example.com");

        Assert.NotEqual(first.McpServerName, second.McpServerName);
    }
}

// `PluginMcpSignInOutcome`'s zero value (AC-243): must be `Unavailable`, not `Authorized` —
// `default(PluginMcpSignInOutcome)` is what an unstubbed fake (`Substitute.For&lt;ICockpitHost&gt;()`'s
// default `Task&lt;T&gt;` answer for any method nobody configured a return for), a missed switch arm, or a
// deserialization gap all produce — none of which have actually signed anything in.
public class PluginMcpSignInOutcomeTests
{
    [Fact]
    public void DefaultValue_IsUnavailable_NeverAuthorized()
    {
        Assert.Equal(PluginMcpSignInOutcome.Unavailable, default(PluginMcpSignInOutcome));
    }
}
