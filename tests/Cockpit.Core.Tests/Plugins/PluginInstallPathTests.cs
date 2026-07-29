using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The zip-slip guard (#14): entries under the destination root are accepted, traversal is rejected.</summary>
public class PluginInstallPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cockpit-install-path", "dest");

    [Fact]
    public void TryResolveSafeEntryPath_RootLevelEntry_ResolvesUnderRoot()
    {
        Assert.True(PluginInstallPath.TryResolveSafeEntryPath(Root, "plugin.json", out var resolved));
        Assert.StartsWith(Path.GetFullPath(Root), resolved);
    }

    [Fact]
    public void TryResolveSafeEntryPath_NestedEntry_ResolvesUnderRoot()
    {
        Assert.True(PluginInstallPath.TryResolveSafeEntryPath(Root, "lib/dependency.dll", out var resolved));
        Assert.StartsWith(Path.GetFullPath(Root), resolved);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("lib/../../escape.dll")]
    public void TryResolveSafeEntryPath_Traversal_Rejected(string entry)
    {
        Assert.False(PluginInstallPath.TryResolveSafeEntryPath(Root, entry, out _));
    }

    [Fact]
    public void TryResolveSafeEntryPath_Empty_Rejected()
    {
        Assert.False(PluginInstallPath.TryResolveSafeEntryPath(Root, "", out _));
    }
}
