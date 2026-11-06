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

    // The second row is the edge case a lexical prefix comparison gets wrong: "foo-evil" reads as a match for root
    // "foo" without a separator on the end of the comparison.
    [Theory]
    [InlineData("victim", "approved-plugin")]
    [InlineData("foo", "foo-evil")]
    public void TryResolve_EntryEscapesViaDotDot_Rejected(string pluginFolder, string sibling)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, pluginFolder)).FullName;
        Directory.CreateDirectory(Path.Combine(_root, sibling));
        File.WriteAllText(Path.Combine(_root, sibling, "entry.dll"), "other-bytes");

        var accepted = PluginEntryPath.TryResolve(folder, $"../{sibling}/entry.dll", out _);

        Assert.False(accepted);
    }

    // The edge case Path.GetFullPath alone gets wrong: it canonicalises lexically, so a symlink inside the
    // folder still reads as contained by spelling while the file it resolves to sits outside it (AC-1160).
    [SkippableFact]
    public void TryResolve_SymlinkInsideFolderPointsOutside_Rejected()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "plugin")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "entry.dll"), "outside-bytes");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, "link"), outside);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, $"This OS cannot create a directory symlink: {error.Message}");
        }

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
