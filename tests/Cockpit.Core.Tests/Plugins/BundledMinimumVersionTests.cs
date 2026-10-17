using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>AC-1500: a bundled plugin is reported only when it is under the bundle's minimum and the operator did not choose that version.</summary>
public class BundledMinimumVersionTests
{
    [Fact]
    public void IsBelowBundledMinimum_OnlyWhenUnderTheMinimum_AndNotChosenByTheOperator()
    {
        Assert.True(PluginVersion.IsBelowBundledMinimum("0.11.8", "0.12.0", versionChosen: false));
        Assert.False(PluginVersion.IsBelowBundledMinimum("0.11.8", "0.12.0", versionChosen: true));
        Assert.False(PluginVersion.IsBelowBundledMinimum("0.12.0", "0.12.0", versionChosen: false));
        Assert.False(PluginVersion.IsBelowBundledMinimum("0.11.8", null, versionChosen: false));
    }
}
