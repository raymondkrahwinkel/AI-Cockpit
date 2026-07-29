using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugin.Depot.Model;

namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// <see cref="DepotConnectionRegistration.McpServerName"/> (AC-243): the fixed <c>"Depot: "</c> prefix Raymond chose
/// so a connection managed here can never collide with (and silently overwrite, since <c>AddMcpServer</c> is an
/// upsert-by-name) an unrelated server an operator configured by hand in the MCP-servers dialog.
/// </summary>
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

/// <summary>
/// <see cref="PluginMcpSignInOutcome"/>'s zero value (AC-243): must be <c>Unavailable</c>, not <c>Authorized</c> —
/// <c>default(PluginMcpSignInOutcome)</c> is what an unstubbed fake (<c>Substitute.For&lt;ICockpitHost&gt;()</c>'s
/// default <c>Task&lt;T&gt;</c> answer for any method nobody configured a return for), a missed switch arm, or a
/// deserialization gap all produce — none of which have actually signed anything in.
/// </summary>
public class PluginMcpSignInOutcomeTests
{
    [Fact]
    public void DefaultValue_IsUnavailable_NeverAuthorized()
    {
        Assert.Equal(PluginMcpSignInOutcome.Unavailable, default(PluginMcpSignInOutcome));
    }
}
