using Cockpit.Core.Mcp;
using FluentAssertions;

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

        filesystem.Template.Transport.Should().Be(McpTransport.Stdio);
        filesystem.Template.Command.Should().Be("npx");
        filesystem.Template.Args.Should().Contain("@modelcontextprotocol/server-filesystem");

        // The last argument is the folder the server is scoped to — a rooted path, not "." or empty.
        var root = filesystem.Template.Args[^1];
        root.Should().NotBeNullOrWhiteSpace();
        Path.IsPathRooted(root).Should().BeTrue();
    }

    [Fact]
    public void Filesystem_DefaultsToLocalOnly_SinceClaudeAlreadyHasFileTools()
    {
        var filesystem = McpServerPresets.All.Single(preset => preset.Label == "Filesystem");

        filesystem.Template.Scope.Should().Be(McpServerScope.LocalOnly);
    }

    [Fact]
    public void All_PresetsAreLaunchable_EachHasATransportTarget()
    {
        McpServerPresets.All.Should().NotBeEmpty();
        McpServerPresets.All.Should().OnlyContain(preset => !string.IsNullOrWhiteSpace(preset.Template.Command));
    }
}
