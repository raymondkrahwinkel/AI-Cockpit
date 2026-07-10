using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The zip-slip guard (#14): entries under the destination root are accepted, traversal is rejected.</summary>
public class PluginInstallPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cockpit-install-path", "dest");

    [Fact]
    public void TryResolveSafeEntryPath_RootLevelEntry_ResolvesUnderRoot()
    {
        PluginInstallPath.TryResolveSafeEntryPath(Root, "plugin.json", out var resolved).Should().BeTrue();
        resolved.Should().StartWith(Path.GetFullPath(Root));
    }

    [Fact]
    public void TryResolveSafeEntryPath_NestedEntry_ResolvesUnderRoot()
    {
        PluginInstallPath.TryResolveSafeEntryPath(Root, "lib/dependency.dll", out var resolved).Should().BeTrue();
        resolved.Should().StartWith(Path.GetFullPath(Root));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("lib/../../escape.dll")]
    public void TryResolveSafeEntryPath_Traversal_Rejected(string entry)
    {
        PluginInstallPath.TryResolveSafeEntryPath(Root, entry, out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolveSafeEntryPath_Empty_Rejected()
    {
        PluginInstallPath.TryResolveSafeEntryPath(Root, "", out _).Should().BeFalse();
    }
}
