using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Update-detection version comparison (#14).</summary>
public class PluginVersionTests
{
    [Theory]
    [InlineData("1.2.0", "1.1.0", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.0.0", "1.2.0", false)]
    [InlineData("2.0", "1.9.9", true)]
    public void IsNewer_NumericVersions_ComparesByValue(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, PluginVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void IsNewer_NonNumeric_FallsBackToInequality()
    {
        Assert.True(PluginVersion.IsNewer("2.0.0-beta", "1.0.0"));
        Assert.False(PluginVersion.IsNewer("1.0.0-beta", "1.0.0-beta"));
    }
}
