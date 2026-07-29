using Cockpit.Infrastructure.Portal;

namespace Cockpit.Infrastructure.Tests.Portal;

/// <summary>
/// The one part of the XDG portal request plumbing a test can reach without a session bus: deriving the object
/// path a portal Request will answer on. Get it wrong and the response signal arrives on a path nothing is
/// listening to — which looks exactly like a portal that never answered, and would strand both the push-to-talk
/// hotkey and a screenshot capture on a wait that never ends.
/// </summary>
public class PortalRequestChannelTests
{
    /// <summary>The portal spec's rule: the caller's unique bus name with the leading ':' stripped and every '.' turned into '_'.</summary>
    [Theory]
    [InlineData(":1.42", "1_42")]
    [InlineData(":1.1234", "1_1234")]
    [InlineData(":1.2.3", "1_2_3")]
    public void TheRequestSender_IsTheUniqueNameWithoutItsColonAndWithDotsAsUnderscores(string localName, string expected) =>
        Assert.Equal(expected, PortalRequestChannel.DeriveRequestSender(localName));

    /// <summary>A name that already arrives without the colon is left alone rather than losing its first character.</summary>
    [Fact]
    public void ANameWithoutALeadingColon_KeepsAllOfItself() =>
        Assert.Equal("1_42", PortalRequestChannel.DeriveRequestSender("1.42"));
}
