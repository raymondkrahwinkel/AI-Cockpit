using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The one shape a paired node's registry rows carry (AC-792, read back by AC-795). Written and parsed in two
/// different assemblies, which is precisely why the rule is one type and this test is what holds the two ends
/// together.
/// </summary>
public class NodeServerNameTests
{
    [Fact]
    public void WhatIsWritten_IsWhatIsReadBack()
    {
        var name = NodeServerName.For("laptop", NodeServerName.SessionsServerName);

        Assert.Equal(("laptop", NodeServerName.SessionsServerName), NodeServerName.Split(name));
        Assert.StartsWith(NodeServerName.PrefixFor("laptop"), name, StringComparison.Ordinal);
    }

    [Theory]
    // An operator's own server, named whatever they liked — never mistaken for a node's row, which would make it
    // a node this cockpit would then try to reach.
    [InlineData("my own server")]
    [InlineData("")]
    // A separator with nothing on one side names neither a node nor an endpoint.
    [InlineData(" · cockpit-node")]
    [InlineData("laptop · ")]
    public void ANameThatIsNotANodesRow_SplitsToNothing(string name) => Assert.Null(NodeServerName.Split(name));

    [Fact]
    public void ANodeNameThatContainsTheSeparator_SplitsAtTheFirstOne()
    {
        // The machine name is what the node reported (`Environment.MachineName`), so it is not this side's to
        // validate. Splitting at the first separator keeps the endpoint — the half this side actually matches on —
        // correct whatever the other half turns out to contain.
        Assert.Equal("odd · name", NodeServerName.Split("odd · name · cockpit-node")?.NodeName);
    }
}
