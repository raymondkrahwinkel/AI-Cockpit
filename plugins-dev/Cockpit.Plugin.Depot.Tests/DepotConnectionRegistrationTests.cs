using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.Tests;

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
