using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="PluginSessionCapabilities.SupportsVision"/> (#64): defaults to false so existing 2-arg
/// construction (every built-in example plugin's <c>Capabilities</c> initializer) keeps compiling
/// unchanged. No example plugin sets it true — <see cref="IPluginSessionDriver.SendUserMessageAsync"/> has
/// no images parameter yet, so a plugin has nothing to back the flag with (#64 fase 2, not fase 1).
/// </summary>
public class PluginSessionCapabilitiesTests
{
    [Fact]
    public void Constructor_DefaultsSupportsVisionToFalse_WhenOmitted()
    {
        var capabilities = new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true);

        capabilities.SupportsVision.Should().BeFalse();
    }
}
