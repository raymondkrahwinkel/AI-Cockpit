using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The pure drift check: only a plugin built against a <em>newer</em> SDK than the host is worth warning about,
/// because that is the one direction that can call a member the host lacks. Older or equal is safe (the contract
/// grows additively), and an unreadable version is not treated as a mismatch.
/// </summary>
public class AbstractionsCompatibilityTests
{
    [Theory]
    [InlineData(1, 3, true)]  // newer — the one direction that can call a member the host lacks
    [InlineData(1, 2, false)] // equal — the common case, no warning
    [InlineData(1, 1, false)] // older — safe, the host is additively backward-compatible
    public void BuiltAgainstNewerHost_OnlyWhenThePluginIsNewerThanTheHost(int major, int minor, bool expected) =>
        Assert.Equal(expected, AbstractionsCompatibility.BuiltAgainstNewerHost(new Version(major, minor, 0, 0), new Version(1, 2, 0, 0)));
}
