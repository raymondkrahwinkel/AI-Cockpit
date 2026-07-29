using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The built-in MCP quick-add catalogue (#26): the filesystem preset gives a local model file access via a
/// stdio npx server scoped to a real folder (not the whole disk), so the fast path stays consent-scoped.
/// </summary>
public class McpServerPresetsTests
{
    [Fact]
    public void All_IncludesAFilesystemPresetScopedToARealFolder()
    {
        var filesystem = McpServerPresets.All.Single(preset => preset.Label == "Filesystem");

        Assert.Equal(McpTransport.Stdio, filesystem.Template.Transport);
        Assert.Equal("npx", filesystem.Template.Command);
        Assert.Contains("@modelcontextprotocol/server-filesystem", filesystem.Template.Args);

        // The last argument is the folder the server is scoped to — a rooted path, not "." or empty.
        var root = filesystem.Template.Args[^1];
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Path.IsPathRooted(root));
    }

    [Fact]
    public void Filesystem_DefaultsToLocalOnly_SinceClaudeAlreadyHasFileTools()
    {
        var filesystem = McpServerPresets.All.Single(preset => preset.Label == "Filesystem");

        Assert.Equal(McpServerScope.LocalOnly, filesystem.Template.Scope);
    }

    [Fact]
    public void All_PresetsAreLaunchable_EachHasATransportTarget()
    {
        Assert.NotEmpty(McpServerPresets.All);
        Assert.All(McpServerPresets.All, preset => Assert.True(!string.IsNullOrWhiteSpace(preset.Template.Command)));
    }
}
