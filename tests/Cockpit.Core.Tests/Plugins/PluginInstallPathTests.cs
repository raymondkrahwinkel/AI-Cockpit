using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The zip-slip guard (#14): entries under the destination root are accepted, traversal is rejected.</summary>
public class PluginInstallPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cockpit-install-path", "dest");

    [Fact]
    public void TryResolveSafeEntryPath_Empty_Rejected()
    {
        Assert.False(PluginInstallPath.TryResolveSafeEntryPath(Root, "", out _));
    }
}
