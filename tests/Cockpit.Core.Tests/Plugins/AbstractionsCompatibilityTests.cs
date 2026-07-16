using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The pure drift check: only a plugin built against a <em>newer</em> SDK than the host is worth warning about,
/// because that is the one direction that can call a member the host lacks. Older or equal is safe (the contract
/// grows additively), and an unreadable version is not treated as a mismatch.
/// </summary>
public class AbstractionsCompatibilityTests
{
    [Fact]
    public void BuiltAgainstNewerHost_WhenPluginIsNewerThanHost_IsTrue() =>
        AbstractionsCompatibility.BuiltAgainstNewerHost(new Version(1, 3, 0, 0), new Version(1, 2, 0, 0))
            .Should().BeTrue();

    [Theory]
    [InlineData(1, 2, 0, 0)] // equal — the common case, no warning
    [InlineData(1, 1, 0, 0)] // older — safe, the host is additively backward-compatible
    [InlineData(1, 0, 0, 0)]
    public void BuiltAgainstNewerHost_WhenPluginIsOlderOrEqual_IsFalse(int major, int minor, int build, int revision) =>
        AbstractionsCompatibility.BuiltAgainstNewerHost(new Version(major, minor, build, revision), new Version(1, 2, 0, 0))
            .Should().BeFalse();

    [Fact]
    public void BuiltAgainstNewerHost_WhenBuiltAgainstIsUnknown_IsFalse() =>
        AbstractionsCompatibility.BuiltAgainstNewerHost(builtAgainst: null, new Version(1, 2, 0, 0))
            .Should().BeFalse();
}
