using Cockpit.Core.Claude;
using FluentAssertions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="SessionCapabilities.SupportsVision"/> (#64): defaults to false so existing 5-arg construction
/// (e.g. <c>OpenAiCompatSessionDriver</c>'s initial value) keeps compiling unchanged, and only the
/// Claude-CLI preset flips it true — Claude is the only built-in driver that actually sends pasted image
/// content blocks today.
/// </summary>
public class SessionCapabilitiesTests
{
    [Fact]
    public void Constructor_DefaultsSupportsVisionToFalse_WhenOmitted()
    {
        var capabilities = new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: true, SupportsPlanMode: true, SupportsThinking: true);

        capabilities.SupportsVision.Should().BeFalse();
    }

    [Fact]
    public void ClaudeCli_SupportsVision_IsTrue()
    {
        SessionCapabilities.ClaudeCli.SupportsVision.Should().BeTrue();
    }
}
