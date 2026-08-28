using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The AC-1159 containment check itself: entryAssembly resolved against its plugin folder.</summary>
public class PluginEntryPathTests : IDisposable
{
    private readonly string _root;

    public PluginEntryPathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cockpit-plugin-entry-path-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // Positive control: without this, a check that rejects everything would read the same as a correct one.
    [Fact]
    public void TryResolve_EntryInOwnFolder_Accepted()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "plugin")).FullName;
        File.WriteAllText(Path.Combine(folder, "Plugin.dll"), "bytes");

        var accepted = PluginEntryPath.TryResolve(folder, "Plugin.dll", out var resolved);

        Assert.True(accepted);
        Assert.Equal(Path.Combine(folder, "Plugin.dll"), resolved);
    }

    [Fact]
    public void TryResolve_EntryEscapesViaDotDot_Rejected()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "victim")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "approved-plugin"));
        File.WriteAllText(Path.Combine(_root, "approved-plugin", "entry.dll"), "real-approved-bytes");

        var accepted = PluginEntryPath.TryResolve(folder, "../approved-plugin/entry.dll", out _);

        Assert.False(accepted);
    }

    [Fact]
    public void TryResolve_EntryIsRooted_Rejected()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "plugin")).FullName;
        var outside = Path.Combine(_root, "elsewhere.dll");
        File.WriteAllText(outside, "elsewhere-bytes");

        var accepted = PluginEntryPath.TryResolve(folder, outside, out _);

        Assert.False(accepted);
    }

    // The edge case a lexical prefix comparison gets wrong: "foo-evil" reads as a match for root "foo"
    // without a separator on the end of the comparison.
    [Fact]
    public void TryResolve_SiblingFolderSharesPrefixWithoutSeparator_Rejected()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "foo")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "foo-evil"));
        File.WriteAllText(Path.Combine(_root, "foo-evil", "entry.dll"), "evil-bytes");

        var accepted = PluginEntryPath.TryResolve(folder, "../foo-evil/entry.dll", out _);

        Assert.False(accepted);
    }

    // The edge case Path.GetFullPath alone gets wrong: it canonicalises lexically, so a symlink inside the
    // folder still reads as contained by spelling while the file it resolves to sits outside it (AC-1160).
    [Fact]
    public void TryResolve_SymlinkInsideFolderPointsOutside_Rejected()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "plugin")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "entry.dll"), "outside-bytes");
        Directory.CreateSymbolicLink(Path.Combine(folder, "link"), outside);

        var accepted = PluginEntryPath.TryResolve(folder, Path.Combine("link", "entry.dll"), out _);

        Assert.False(accepted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
