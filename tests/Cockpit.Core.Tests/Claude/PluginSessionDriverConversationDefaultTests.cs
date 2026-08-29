using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="IPluginSessionDriver.Conversation"/>'s default implementation (AC-408) — proven directly against
/// <see cref="FakePluginSessionDriver"/>, which does not override it, so every already-compiled SDK driver reports
/// correctly with no change of its own.
/// </summary>
public class PluginSessionDriverConversationDefaultTests
{
    [Fact]
    public void Conversation_IsUnknown_BeforeTheDriverHasASessionId()
    {
        IPluginSessionDriver driver = new FakePluginSessionDriver();

        Assert.Equal(PluginConversationId.Unknown, driver.Conversation);
    }

}
